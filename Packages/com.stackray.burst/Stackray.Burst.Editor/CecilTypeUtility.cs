﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Scripting;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Stackray.Burst.Editor {

  public struct CallReference {
    public GenericInstanceType Type;
    public MethodDefinition EntryMethod;

    public override string ToString() {
      return $"{Type?.FullName} With Entry -> {EntryMethod?.Name}";
    }
  }

  public static class CecilTypeUtility {

    public static IEnumerable<string> GetAssemblyPaths(IEnumerable<string> keywords, bool exclude = false) {
      return AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => keywords.Any(ex => (a.FullName.IndexOf(ex, StringComparison.InvariantCultureIgnoreCase) >= 0) != exclude ||
                                        a.ManifestModule.Name == ex != exclude))
                                        .Select(a => a.Location);
    }

    public static AssemblyDefinition CreateAssembly(string name, IEnumerable<TypeReference> types) {
      var assembly = AssemblyDefinition.CreateAssembly(
        new AssemblyNameDefinition(
          name,
          new Version(1, 0, 0, 0)),
          name,
          ModuleKind.Dll);
      AddTypes(assembly, name, types);
      return assembly;
    }

    public static TypeReference MakeGenericType(TypeReference type, IEnumerable<TypeReference> arguments) {
      if (type.GenericParameters.Count != arguments.Count())
        throw new ArgumentException();

      var instance = new GenericInstanceType(type);
      foreach (var argument in arguments)
        instance.GenericArguments.Add(argument);

      return instance;
    }

    public static MethodReference MakeGeneric(MethodReference method, IEnumerable<TypeReference> arguments) {
      if (method == null)
        return null;
      var reference = new MethodReference(method.Name, method.ReturnType) {
        DeclaringType = MakeGenericType(method.DeclaringType, arguments),
        HasThis = method.HasThis,
        ExplicitThis = method.ExplicitThis,
        CallingConvention = method.CallingConvention,
      };

      foreach (var parameter in method.Parameters)
        reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

      foreach (var genericParameter in method.GenericParameters)
        reference.GenericParameters.Add(new GenericParameter(genericParameter.Name, reference));

      return reference;
    }

    public static MethodReference GetConstructor(TypeReference type) {
      return MakeGeneric(
          type.Resolve()?.GetConstructors().FirstOrDefault(),
          (type as GenericInstanceType)?.GenericArguments ?? Enumerable.Empty<TypeReference>());
    }

    public static void AddTypes(AssemblyDefinition assembly, string name, IEnumerable<TypeReference> types) {
      var module = assembly.MainModule;
      var mainType = new TypeDefinition(name, name,
        TypeAttributes.Class | TypeAttributes.Public, module.TypeSystem.Object);
      module.Types.Add(mainType);
      var mainMethod = new MethodDefinition(
        name,
        MethodAttributes.Public | MethodAttributes.Static,
        module.ImportReference(typeof(void)));
      var preserve = module.ImportReference(
        typeof(PreserveAttribute).GetConstructor(Type.EmptyTypes));
      mainMethod.CustomAttributes.Add(new CustomAttribute(preserve));
      mainType.Methods.Add(mainMethod);

      var toStringMethod = module.ImportReference(
        typeof(object).GetMethod(nameof(object.ToString)));
      mainType.Module.ImportReference(toStringMethod);

      var iL = mainMethod.Body.GetILProcessor();
      for (var i = 0; i < types.Count(); ++i) {
        var element = types.ElementAt(i);
        iL.Emit(OpCodes.Nop);
        var constructor = GetConstructor(element);
        if (constructor != null) {
          // If we were able to resolve the constructor lets create a new object
          iL.Emit(OpCodes.Newobj, module.ImportReference(constructor));
        } else {
          var typeReference = module.ImportReference(types.ElementAt(i));
          var localVar = new VariableDefinition(typeReference);
          mainMethod.Body.Variables.Add(localVar);
          iL.Emit(OpCodes.Ldloca_S, localVar);
          iL.Emit(OpCodes.Dup);
          iL.Emit(OpCodes.Initobj, typeReference);
          iL.Emit(OpCodes.Constrained, typeReference);
          iL.Emit(OpCodes.Callvirt, toStringMethod);
        }
        iL.Emit(OpCodes.Pop);
      }
      iL.Emit(OpCodes.Ret);
    }

    public static MethodDefinition GetMethodDefinition(AssemblyDefinition assembly, Type type, string methodName) {
      return GetMethodDefinitions(assembly)
        .Where(t => t.IsPublic && t.Name == methodName && GetType(t.DeclaringType) == type)
        .Single();
    }

    public static IEnumerable<TypeDefinition> GetTypeDefinitions(AssemblyDefinition assembly) {
      return assembly.Modules.SelectMany(m => m.Types)
      .SelectMany(t => new[] { t }.Union(t.NestedTypes))
      .Where(t => t.Name != "<Module>" || t.Name.Contains("AnonymousType"))
      .ToArray();
    }

    public static IEnumerable<MethodDefinition> GetMethodDefinitions(AssemblyDefinition assembly) {
      return assembly.Modules.SelectMany(m => m.Types)
      .SelectMany(t => new[] { t }.Union(t.NestedTypes))
      .Where(t => t.Name != "<Module>" || t.Name.Contains("AnonymousType"))
      .SelectMany(t => t.Methods)
      .ToArray();
    }

    public static IEnumerable<CallReference> GetGenericInstanceCalls(TypeDefinition type, Func<GenericInstanceType, bool> predicate) {
      var result = new HashSet<CallReference>();
      foreach (var method in type.Methods.Where(m => m.Body != null && (m.HasGenericParameters || type.HasGenericParameters)))
        foreach (var genericJob in GetGenericInstanceTypes(method.Body, predicate))
          result.Add(new CallReference {
            Type = genericJob,
            EntryMethod = method
          });
      return result;
    }

    static IEnumerable<GenericInstanceType> GetGenericInstanceTypes(MethodBody methodBody, Func<GenericInstanceType, bool> predicate) {
      return methodBody.Instructions
        .Where(i => i.Operand is GenericInstanceType && predicate.Invoke(i.Operand as GenericInstanceType))
        .Select(i => i.Operand as GenericInstanceType);
    }

    static TypeReference GetNestedRootType(TypeReference type) {
      if (!type.IsNested)
        return type;
      return GetNestedRootType(type.DeclaringType);
    }

    public static Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>> GetMethodTypeLookup(IEnumerable<AssemblyDefinition> assemblies) {
      var callerTree = new Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>>();
      foreach (var assembly in assemblies)
        GetMethodTypeLookup(callerTree, assembly);
      return callerTree;
    }

    static void GetMethodTypeLookup(Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>> lookup, AssemblyDefinition assembly) {
      foreach (var type in GetTypeDefinitions(assembly))
        GetMethodTypeLookup(lookup, type, GetMethodReference);
    }

    public static Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>> GetTypeCallLookup(IEnumerable<AssemblyDefinition> assemblies) {
      var callerTree = new Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>>();
      foreach (var assembly in assemblies)
        GetTypeCallLookup(callerTree, assembly);
      return callerTree;
    }

    static void GetTypeCallLookup(Dictionary<MethodDefinition, List<(MethodReference, MethodDefinition)>> lookup, AssemblyDefinition assembly) {
      foreach (var type in GetTypeDefinitions(assembly))
        GetMethodTypeLookup(lookup, type, GetMethodReference);
    }

    public static Dictionary<MethodDefinition, List<(GenericInstanceMethod, MethodDefinition)>> GetGenericMethodTypeLookup(IEnumerable<AssemblyDefinition> assemblies) {
      var callerTree = new Dictionary<MethodDefinition, List<(GenericInstanceMethod, MethodDefinition)>>();
      foreach (var assembly in assemblies)
        GetGenericMethodTypeLookup(callerTree, assembly);
      return callerTree;
    }

    static void GetGenericMethodTypeLookup(Dictionary<MethodDefinition, List<(GenericInstanceMethod, MethodDefinition)>> lookup, AssemblyDefinition assembly) {
      foreach (var type in GetTypeDefinitions(assembly))
        GetMethodTypeLookup(lookup, type, GetGenericMethodReference);
    }

    static MethodDefinition GetMatchingMethod(TypeDefinition type, MethodDefinition method) {
      return MetadataResolver.GetMethod(type.Methods, method);
    }

    static void GetMethodTypeLookup<T>(Dictionary<MethodDefinition, List<(T, MethodDefinition)>> lookup, TypeDefinition type, Func<object, T> predicate)
      where T : MethodReference {

      foreach (var method in type.Methods) {
        var body = method.Body;
        if (body == null)
          continue;

        foreach (var instruction in body.Instructions) {
          var methodRef = predicate.Invoke(instruction.Operand);
          if (methodRef != null) {
            var key = methodRef.Resolve();
            if (key == null)
              continue;
            if (!lookup.TryGetValue(key, out var methods)) {
              methods = new List<(T, MethodDefinition)>();
              lookup.Add(key, methods);
            }
            methods.Add((methodRef, method));
          }
        }
      }
    }

    static MethodReference GetMethodReference(object input) {
      return input as MethodReference;
    }

    static GenericInstanceMethod GetGenericMethodReference(object input) {
      var method = input as MethodReference;
      return (method?.IsGenericInstance ?? false) || (method?.DeclaringType?.IsGenericInstance ?? false) ?
        (input as GenericInstanceMethod) ?? new GenericInstanceMethod(method) : null;
    }

    public static IEnumerable<TypeReference> ResolveCalls(IEnumerable<CallReference> calls, IEnumerable<AssemblyDefinition> assemblies) {
      var methodLookup = GetGenericMethodTypeLookup(assemblies);
      var resolvedCalls = Enumerable.Empty<CallReference>();
      foreach (var call in calls)
        resolvedCalls = resolvedCalls.Concat(ResolveCall(methodLookup, call)).ToArray();
      return ResolveGenericTypes(resolvedCalls, assemblies);
    }

    static IEnumerable<CallReference> ResolveCall(Dictionary<MethodDefinition, List<(GenericInstanceMethod, MethodDefinition)>> methodLookup, CallReference callReference) {
      var res = new List<CallReference>();
      var key = callReference.EntryMethod;
      if (methodLookup.TryGetValue(key, out var methods)) {
        foreach (var (inst, method) in methods)
          res.AddRange(
            ResolveCall(methodLookup, new CallReference {
              Type = ResolveGenericType(inst, method.DeclaringType, callReference.Type) as GenericInstanceType,
              EntryMethod = method
            }));
        return res;
      }
      return new[] { callReference };
    }

    #region Get Hierarchy
    static IEnumerable<TypeReference> GetTypeHierarchy(TypeReference type, TypeReference baseType) {
      return new[] { baseType }
      .Concat(GetHierarchy(type, GetNestedRootType(baseType)));
    }

    static IEnumerable<TypeReference> GetHierarchy(TypeReference type, TypeReference baseType) {
      var result = new[] { type };
      if (type == null || type.Resolve()?.FullName == baseType.FullName || GetNestedRootType(type).FullName == GetNestedRootType(baseType).FullName)
        return result;
      return GetHierarchy(type.Resolve()?.BaseType, baseType)
        .Concat(result);
    }
    #endregion Get Hierarchy

    #region Resolve Generic Types

    public static IEnumerable<TypeReference> ResolveGenericTypes(IEnumerable<CallReference> types, IEnumerable<AssemblyDefinition> assemblies) {
      var result = Enumerable.Empty<TypeReference>();
      foreach (var assembly in assemblies)
        result = result.Concat(ResolveGenericTypes(types, assembly));
      return result
        .GroupBy(t => t.FullName)
        .Select(g => g.First())
        .ToArray();
    }

    static IEnumerable<TypeReference> ResolveGenericTypes(IEnumerable<CallReference> calls, AssemblyDefinition assembly) {
      var possibleConcreteTypes = GetPossibleConcreteTypes(assembly);
      var result = new HashSet<TypeReference>();
      foreach (var call in calls)
        if (call.Type.GenericArguments.Any(a => a.IsGenericParameter))
          foreach (var concreteType in possibleConcreteTypes)
            result.Add(ResolveGenericType(concreteType, call));
        else
          result.Add(call.Type);
      result.Remove(default);
      return result;
    }

    static IEnumerable<TypeReference> GetPossibleConcreteTypes(IEnumerable<AssemblyDefinition> assemblies) {
      return assemblies.SelectMany(a => GetPossibleConcreteTypes(a)).ToArray();
    }

    static IEnumerable<TypeReference> GetPossibleConcreteTypes(AssemblyDefinition assembly) {
      // Generic types with concrete parameters that exist in method bodies
      var t1 = GetTypeDefinitions(assembly)
        .SelectMany(t => t.Methods)
        .Where(m => m.Body != null)
        .SelectMany(m => m.Body.Instructions)
        .Where(i => i.Operand is GenericInstanceType)
        .Select(i => i.Operand as GenericInstanceType)
        .Where(t => !t.ContainsGenericParameter);

      // Concrete types that extend from generic types
      var t2 = GetTypeDefinitions(assembly)
        .Where(t => t.IsClass && t.BaseType.IsGenericInstance && !t.ContainsGenericParameter)
        .Select(t => t.BaseType as GenericInstanceType);

      // Generic type calls with concrete parameters
      var t3 = GetTypeDefinitions(assembly)
        .SelectMany(t => t.Methods)
        .Where(m => m.Body != null)
        .SelectMany(m => m.Body.Instructions)
        .Where(i => i.Operand is MethodReference)
        .Select(i => i.Operand as MethodReference)
        .Select(m => m.DeclaringType)
        .Where(t => t.IsGenericInstance && !t.ContainsGenericParameter);

      return t1.Union(t2).Union(t3)
          .GroupBy(t => t.FullName)
          .Select(g => g.First())
          .ToArray();
    }

    static TypeReference ResolveGenericType(TypeReference type, CallReference callReference) {
      var baseType = CreateGenericInstanceType(callReference.Type);
      if (baseType.DeclaringType == null) {
        // TODO: use hierarchy 
        var resolveBase = ResolveGenericType(type, callReference.EntryMethod.DeclaringType);
        var genericArguments = ResolveGenericArgumentTypes(resolveBase, baseType);
        for (var i = 0; i < genericArguments.Count(); ++i)
          baseType.GenericArguments[i] = genericArguments.ElementAt(i);
        return !baseType.ContainsGenericParameter ? baseType : default;
      }
      var resolvedType = ResolveGenericType(type, baseType);
      return !resolvedType.ContainsGenericParameter ? resolvedType : default;
    }

    static TypeReference ResolveGenericType(TypeReference type, TypeReference baseType) {
      var genericInst = CreateGenericInstanceType(baseType);
      var argumentsHierarchy = GetTypeHierarchy(type, baseType)
        .Select(t => GetGenericArguments(t));
      var genericArguments = ResolveGenericArgumentTypes(argumentsHierarchy);
      for (var i = 0; i < genericArguments.Count(); ++i)
        genericInst.GenericArguments[i] = genericArguments.ElementAt(i);
      return genericInst;
    }

    static TypeReference ResolveGenericType(MethodReference method, TypeReference methodOwner, TypeReference baseType) {
      var genericInst = CreateGenericInstanceType(baseType);
      if (genericInst == null || method == null)
        return baseType;
      var genericArguments = ResolveGenericArgumentTypes(GetGenericArguments(method, methodOwner), GetGenericArguments(genericInst));
      for (var i = 0; i < genericArguments.Count(); ++i)
        genericInst.GenericArguments[i] = genericArguments.ElementAt(i);
      return genericInst;
    }

    static TypeReference ResolveGenericType(IEnumerable<TypeReference> argumentTypes, TypeReference baseType) {
      var genericInst = CreateGenericInstanceType(baseType);
      var genericArguments = ResolveGenericArgumentTypes(argumentTypes, GetGenericArguments(baseType));
      for (var i = 0; i < genericArguments.Count(); ++i)
        genericInst.GenericArguments[i] = genericArguments.ElementAt(i);
      return genericInst;
    }

    #endregion Resolve Generic Types

    #region Resolve Generic Argument Types
    static IEnumerable<TypeReference> ResolveGenericArgumentTypes(IEnumerable<IEnumerable<TypeReference>> argumentHierarchy) {
      var resolvedArguments = argumentHierarchy.First();
      for (var i = 1; i < argumentHierarchy.Count(); ++i)
        resolvedArguments = ResolveGenericArgumentTypes(argumentHierarchy.ElementAt(i), resolvedArguments);
      return resolvedArguments;
    }

    static IEnumerable<TypeReference> ResolveGenericArgumentTypes(TypeReference type, TypeReference baseType) {
      return ResolveGenericArgumentTypes(GetGenericArguments(type), GetGenericArguments(baseType));
    }

    static IEnumerable<TypeReference> ResolveGenericArgumentTypes(IEnumerable<TypeReference> argumentTypes, IEnumerable<TypeReference> baseArgumentTypes) {
      var result = new List<TypeReference>();
      for (var i = 0; i < baseArgumentTypes.Count(); ++i) {
        var genericParameter = baseArgumentTypes.ElementAt(i);

        if (genericParameter is GenericParameter) {
          var resolvedParameter =
              ResolveGenericParameter(genericParameter as GenericParameter, argumentTypes, baseArgumentTypes);
          if (resolvedParameter != null)
            result.Add(resolvedParameter);
          else
            return Enumerable.Empty<TypeReference>();

        } else if (genericParameter is GenericInstanceType) {
          var resolvedParameter = ResolveGenericType(argumentTypes, genericParameter);
          if (resolvedParameter != null)
            result.Add(resolvedParameter);
          else
            return Enumerable.Empty<TypeReference>();

        } else
          result.Add(genericParameter);
      }
      return result;
    }

    static TypeReference ResolveGenericParameter(GenericParameter genericParameter, IEnumerable<TypeReference> argumentTypes, IEnumerable<TypeReference> baseArgumentTypes) {
      var baseMethodTypesOffset = /*baseArgumentTypes
        .FirstOrDefault(a => a is GenericParameter)?.DeclaringType?.GenericParameters.Count() ??*/
        argumentTypes
        .FirstOrDefault(a => a is GenericParameter)?.DeclaringType?.GenericParameters.Count() ?? 0;
      var pos = genericParameter.Position;
      if (genericParameter.Type == GenericParameterType.Type && pos < argumentTypes.Count())
        return argumentTypes.ElementAt(pos);
      else if (genericParameter.Type == GenericParameterType.Method && baseMethodTypesOffset + pos < argumentTypes.Count())
        return argumentTypes.ElementAt(baseMethodTypesOffset + pos);
      return null;
    }

    #endregion Resolve Generic Argument Types

    static IEnumerable<TypeReference> GetGenericArguments(MethodReference method) {
      return GetGenericArguments(method, method.DeclaringType);
    }

    static IEnumerable<TypeReference> GetGenericArguments(MethodReference method, TypeReference owner) {
      var ownerArgs = (owner?.GenericParameters ?? Enumerable.Empty<TypeReference>())
        .Concat((owner as GenericInstanceType)?.GenericArguments ?? Enumerable.Empty<TypeReference>());
      if (!ownerArgs.Any())
        ownerArgs = ownerArgs.Concat((method.DeclaringType as GenericInstanceType)?.GenericArguments ?? Enumerable.Empty<TypeReference>());
      return ownerArgs
        .Concat((owner?.Resolve()?.BaseType as GenericInstanceType)?.GenericArguments ?? Enumerable.Empty<TypeReference>())
        .Concat((method as GenericInstanceMethod)?.GenericArguments ?? Enumerable.Empty<TypeReference>())
                ?? Enumerable.Empty<TypeReference>();
    }

    static IEnumerable<TypeReference> GetGenericArguments(TypeReference type) {
      return (type as GenericInstanceType)?.GenericArguments ??
        type?.GenericParameters ?? Enumerable.Empty<TypeReference>()
                ?? Enumerable.Empty<TypeReference>();
    }

    static GenericInstanceType CreateGenericInstanceType(TypeReference instance) {
      if (instance == null)
        return null;
      var newInstance = new GenericInstanceType(instance.Resolve());
      foreach (var arg in GetGenericArguments(instance))
        newInstance.GenericArguments.Add(arg);
      return newInstance;
    }

    public static Type GetType(TypeReference type) {
      var reflectedName = GetReflectionName(type);
      return Type.GetType(reflectedName, false);
    }

    static string GetReflectionName(TypeReference type) {
      if (type.IsGenericInstance) {
        var nameSpace = GetNameSpace(type);
        var declaringName = type.DeclaringType?.FullName ?? type.Namespace;
        var assemblyName = type.Module.Assembly.Name.FullName;
        declaringName = declaringName.Replace('/', '+');
        var nested = type.DeclaringType != null ? "+" : ".";
        return string.Format("{0}{1}{2}, {3}",
          declaringName,
          nested,
          type.Name,
          assemblyName);
      }
      return type.FullName;
    }

    static string GetReflectionName(TypeDefinition type) {
      if (type.IsGenericInstance) {
        var nameSpace = GetNameSpace(type);
        var declaringName = type.DeclaringType?.FullName ?? type.Namespace;
        var assemblyName = type.Module.Assembly.Name.FullName;
        declaringName = declaringName.Replace('/', '+');
        var nested = type.DeclaringType != null ? "+" : ".";
        return string.Format("{0}{1}{2}, {3}",
          declaringName,
          nested,
          type.Name,
          assemblyName);
      }
      return type.FullName;
    }

    static string GetNameSpace(TypeReference type) {
      if (type == null)
        return string.Empty;

      return string.IsNullOrEmpty(type.Namespace) ? GetNameSpace(type.DeclaringType) : type.Namespace;
    }
  }
}

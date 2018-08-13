using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AssemblyComparer {
	public class AssemblyAnalyzer: MarshalByRefObject {
		private static string RenderParameters(IEnumerable<ParameterInfo> parameters) {
			return string.Join(",", parameters.OrderBy(p => p.Position).Select(p => p.ParameterType.GetFullName()));
		}

		private static string RenderInterfaces(Type type, IReadOnlyDictionary<Type, bool> publicTypes) {
			var result = string.Join(",", type.GetInterfaces().Where(t => t.Assembly == type.Assembly ? publicTypes.ContainsKey(t) : (GetObfuscationAttribute(t.GetCustomAttributesData())?.Exclude).GetValueOrDefault(true)).Select(i => i.GetFullName()));
			if (result.Length > 0) {
				return "|IMPLEMENTS:"+result;
			}
			return "";
		}

		private static ObfuscationAttribute GetObfuscationAttribute(MemberInfo member) {
			return GetObfuscationAttribute(member.GetCustomAttributesData());
		}

		private static ObfuscationAttribute GetObfuscationAttribute(IEnumerable<CustomAttributeData> data) {
			return data.InstantiateAttributes<ObfuscationAttribute>().SingleOrDefault();
		}

		public string[] Analyze(string assemblyFile) {
			AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve +=
					(sender, args) => {
						var assemblyName = new AssemblyName(AppDomain.CurrentDomain.ApplyPolicy(args.Name));
						try {
							return Assembly.ReflectionOnlyLoad(assemblyName.FullName);
						} catch (Exception ex) {
							var fileName = Path.Combine(Path.GetDirectoryName(assemblyFile), assemblyName.Name+".exe");
							if (!File.Exists(fileName)) {
								fileName = Path.ChangeExtension(fileName, ".dll");
							}
							return Assembly.ReflectionOnlyLoadFrom(fileName);
						}
					};
			var assembly = Assembly.ReflectionOnlyLoadFrom(assemblyFile);
			var isAssemblyPublic = !assembly
					.GetCustomAttributesData()
					.InstantiateAttributes<ObfuscateAssemblyAttribute>()
					.Select(a => a.AssemblyIsPrivate)
					.SingleOrDefault();
			var publicTypes = PublicTypes(assembly
							.GetExportedTypes()
							.Where(t => !t.IsNested), isAssemblyPublic)
					.ToDictionary(p => p.Key, p => p.Value);
			return publicTypes
					.Keys
					.SelectMany(t => ExpandType(t, publicTypes))
					.ToArray();
		}

		private IEnumerable<string> ExpandType(Type type, IReadOnlyDictionary<Type, bool> publicTypes) {
			var isMemberPublicDefault = publicTypes[type];
			var typeName = type.GetFullName();
			var builder = new StringBuilder();
			builder.Append("TYPE|");
			builder.Append(typeName);
			if (type.BaseType != null) {
				builder.Append("|INHERITS:");
				builder.Append(type.BaseType.GetFullName());
			}
			builder.Append(RenderInterfaces(type, publicTypes));
			yield return builder.ToString();
			foreach (var member in type.GetMembers(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.DeclaredOnly).Where(t => !(t is Type))) {
				if (GetObfuscationAttribute(member).IsPublic(isMemberPublicDefault)) {
					builder.Length = 0;
					switch (member) {
					case FieldInfo field:
						if ((field.DeclaringType?.IsEnum).GetValueOrDefault() && !field.IsStatic) {
							// Don't care about Enum value field 
							break;
						}
						builder.Append("FIELD|");
						if (field.IsLiteral) {
							builder.Append("CONST|");
						} else {
							if (field.IsStatic) {
								builder.Append("STATIC|");
							}
							if (field.IsInitOnly) {
								builder.Append("READONLY|");
							}
						}
						builder.Append(typeName);
						builder.Append(".");
						builder.Append(field.Name);
						builder.Append(":");
						builder.Append(field.FieldType.GetFullName());
						yield return builder.ToString();
						break;
					case PropertyInfo property:
						builder.Append("PROPERTY|");
						if (property.GetAccessors().First().IsStatic) {
							builder.Append("STATIC|");
						}
						var parameters = property.GetIndexParameters();
						if (parameters.Length > 0) {
							builder.Append("[");
							builder.Append(string.Join(",", parameters.OrderBy(p => p.Position).Select(p => p.ParameterType.GetFullName())));
							builder.Append("]|");
						}
						builder.Append(typeName);
						builder.Append(".");
						builder.Append(property.Name);
						builder.Append(":");
						builder.Append(property.PropertyType.GetFullName());
						var getMethod = property.GetGetMethod();
						if ((getMethod != null) && GetObfuscationAttribute(getMethod.GetCustomAttributesData()).IsPublic(isMemberPublicDefault)) {
							yield return builder+"|GET";
						}
						var setMethod = property.GetSetMethod();
						if ((setMethod != null) && GetObfuscationAttribute(setMethod.GetCustomAttributesData()).IsPublic(isMemberPublicDefault)) {
							yield return builder+"|SET";
						}
						break;
					case MethodInfo method:
						if (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal)) {
							break;
						}
						builder.Append("METHOD|");
						if (method.IsStatic) {
							builder.Append("STATIC|");
						}
						builder.Append(typeName);
						builder.Append(".");
						builder.Append(method.Name);
						builder.Append("(");
						builder.Append(RenderParameters(method.GetParameters()));
						builder.Append("):");
						builder.Append(method.ReturnType.GetFullName());
						yield return builder.ToString();
						break;
					case ConstructorInfo ctor:
						if (ctor.IsStatic) {
							// Static constructor is self-contained and not part of the public API
							break;
						}
						builder.Append("CTOR|");
						builder.Append(typeName);
						builder.Append("(");
						builder.Append(RenderParameters(ctor.GetParameters()));
						builder.Append(")");
						yield return builder.ToString();
						break;
					case EventInfo @event:
						builder.Append("EVENT|");
						if (@event.AddMethod.IsStatic) {
							builder.Append("STATIC|");
						}
						builder.Append(typeName);
						builder.Append(".");
						builder.Append(@event.Name);
						builder.Append(":");
						builder.Append(@event.EventHandlerType.GetFullName());
						yield return builder.ToString();
						break;
					}
				}
			}
		}

		private IEnumerable<KeyValuePair<Type, bool>> PublicTypes(IEnumerable<Type> types, bool isParentPublic) {
			foreach (var type in types) {
				var attr = GetObfuscationAttribute(type);
				if (!attr.IsPublic(isParentPublic)) {
					// Type is private (obfuscation is allowed)
					continue;
				}
				var isMemberPublicDefault = (attr != null) && attr.ApplyToMembers ? attr.Exclude : isParentPublic;
				yield return new KeyValuePair<Type, bool>(type, isMemberPublicDefault);
				foreach (var nestedType in PublicTypes(type.GetNestedTypes(), isMemberPublicDefault)) {
					yield return nestedType;
				}
			}
		}
	}
}

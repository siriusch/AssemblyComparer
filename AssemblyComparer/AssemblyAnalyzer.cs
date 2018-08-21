using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web;

namespace AssemblyComparer {
	public class AssemblyAnalyzer: MarshalByRefObject {
		private enum TypeVisibility {
			Public,
			MembersPrivate,
			Private
		}

		private static string RenderParameters(IEnumerable<ParameterInfo> parameters) {
			return string.Join(",", parameters.OrderBy(p => p.Position).Select(p => p.ParameterType.GetFullName()));
		}

		private static string RenderInterfaces(Type type, IReadOnlyDictionary<Type, TypeVisibility> types) {
			var result = string.Join(",", type.GetInterfaces().Where(t => {
				return t.Assembly == type.Assembly ? types[t.IsGenericType ? t.GetGenericTypeDefinition() : t] == TypeVisibility.Public : (GetObfuscationAttribute(t.GetCustomAttributesData())?.Exclude).GetValueOrDefault(true);
			}).Select(i => i.GetFullName()));
			if (result.Length > 0) {
				return "|IMPLEMENTS:" + result;
			}
			return "";
		}

		private static ObfuscationAttribute GetObfuscationAttribute(IEnumerable<CustomAttributeData> data) {
			return data.InstantiateAttributes<ObfuscationAttribute>().SingleOrDefault(a => string.IsNullOrEmpty(a.Feature));
		}

		private static bool IsDebugConditional(IEnumerable<CustomAttributeData> data) {
			return data.InstantiateAttributes<ConditionalAttribute>().Any(a => string.Equals(a.ConditionString, "DEBUG", StringComparison.OrdinalIgnoreCase));
		}

		private static IEnumerable<KeyValuePair<Type, TypeVisibility>> PublicTypes(IEnumerable<Type> types, bool defaultPublic) {
			foreach (var type in types) {
				var attrData = type.GetCustomAttributesData();
				var obfuscationAttr = GetObfuscationAttribute(attrData);
				var isMemberPublicDefault = (obfuscationAttr != null) && obfuscationAttr.ApplyToMembers ? obfuscationAttr.Exclude : defaultPublic;
				yield return new KeyValuePair<Type, TypeVisibility>(type, (obfuscationAttr?.Exclude ?? defaultPublic) ? isMemberPublicDefault ? TypeVisibility.Public : TypeVisibility.MembersPrivate : TypeVisibility.Private);
				foreach (var nestedType in PublicTypes(type.GetNestedTypes(), isMemberPublicDefault)) {
					yield return nestedType;
				}
			}
		}

		private readonly bool checkInheritance;

		public AssemblyAnalyzer(bool checkInheritance) {
			this.checkInheritance = checkInheritance;
		}

		public KeyValuePair<string, bool>[] Analyze(string assemblyFile, string assemblyLoadPath = null) {
			return this.AnalyzeInternal(Assembly.ReflectionOnlyLoadFrom, assemblyFile, assemblyLoadPath ?? Path.GetDirectoryName(assemblyFile));
		}

		public KeyValuePair<string, bool>[] Analyze(byte[] assemblyData, string assemblyLoadPath) {
			return this.AnalyzeInternal(Assembly.ReflectionOnlyLoad, assemblyData, assemblyLoadPath);
		}

		private KeyValuePair<string, bool>[] AnalyzeInternal<T>(Func<T, Assembly> getAssembly, T arg, string assemblyLoadPath) {
			AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve +=
					(sender, args) => {
						var assemblyName = new AssemblyName(AppDomain.CurrentDomain.ApplyPolicy(args.Name));
						try {
							return Assembly.ReflectionOnlyLoad(assemblyName.FullName);
						} catch (Exception) {
							var fileName = Path.Combine(assemblyLoadPath, assemblyName.Name + ".exe");
							if (!File.Exists(fileName)) {
								fileName = Path.ChangeExtension(fileName, ".dll");
							}
							return Assembly.ReflectionOnlyLoadFrom(fileName);
						}
					};
			var assembly = getAssembly(arg);
			var isAssemblyPublic = !assembly
					.GetCustomAttributesData()
					.InstantiateAttributes<ObfuscateAssemblyAttribute>()
					.Select(a => a.AssemblyIsPrivate)
					.SingleOrDefault();
			var publicTypes = PublicTypes(assembly
							.GetExportedTypes()
							.Where(t => !t.IsNested), isAssemblyPublic)
					.ToDictionary(p => p.Key, p => p.Value);
			var ignores = assembly
					.GetCustomAttributesData()
					.InstantiateAttributes<ObfuscationAttribute>()
					.Where(a => a.Feature.StartsWith("AssemblyCompareIgnore ", StringComparison.OrdinalIgnoreCase))
					.Select(a => new KeyValuePair<string, bool>(a.Feature.Substring(22).Trim(), !a.Exclude));
			return publicTypes
					.Keys
					.SelectMany(t => this.ExpandType(t, publicTypes))
					.Concat(ignores)
					.ToArray();
		}

		private IEnumerable<KeyValuePair<string, bool>> ExpandType(Type type, IReadOnlyDictionary<Type, TypeVisibility> publicTypes) {
			var typeVisibility = publicTypes[type];
			var typeName = type.GetFullName();
			var builder = new StringBuilder();
			builder.Append("TYPE|");
			builder.Append(typeName);
			var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public;
			if (this.checkInheritance && type.BaseType != null) {
				builder.Append("|INHERITS:");
				builder.Append(type.BaseType.GetFullName());
				bindingFlags = bindingFlags | BindingFlags.DeclaredOnly;
			}
			builder.Append(RenderInterfaces(type, publicTypes));
			yield return new KeyValuePair<string, bool>(builder.ToString(), typeVisibility != TypeVisibility.Private);
			foreach (var member in type
					.GetMembers(bindingFlags)
					.Where(t => !(t is Type))) {
				var customAttributesData = member.GetCustomAttributesData();
				var isPublic = !IsDebugConditional(customAttributesData) && GetObfuscationAttribute(customAttributesData).IsPublic(typeVisibility == TypeVisibility.Public);
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
					if (field.IsLiteral) {
						builder.Append("=");
						var obj = field.GetRawConstantValue();
						// ReSharper disable once ConditionIsAlwaysTrueOrFalse
						if (obj == null) {
							builder.Append("null");
						} else if (obj.GetType().IsPrimitive && !(obj is string) && !(obj is char)) {
							builder.Append(Convert.ToString(obj, CultureInfo.InvariantCulture));
						} else {
							var str = HttpUtility.JavaScriptStringEncode(obj is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : obj.ToString(), true);
							builder.Append(str.Replace("\uFFFE", @"\uFFFE").Replace("\uFFFF", @"\uFFFF"));
						}
					}
					yield return new KeyValuePair<string, bool>(builder.ToString(), isPublic);
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
					if (getMethod != null) {
						yield return new KeyValuePair<string, bool>(builder + "|GET", isPublic && GetObfuscationAttribute(getMethod.GetCustomAttributesData()).IsPublic(typeVisibility == TypeVisibility.Public));
					}
					var setMethod = property.GetSetMethod();
					if ((setMethod != null)) {
						yield return new KeyValuePair<string, bool>(builder + "|SET", isPublic && GetObfuscationAttribute(setMethod.GetCustomAttributesData()).IsPublic(typeVisibility == TypeVisibility.Public));
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
					if (method.IsGenericMethodDefinition) {
						builder.Append("`");
						builder.Append(method.GetGenericArguments().Length);
					}
					builder.Append("(");
					builder.Append(RenderParameters(method.GetParameters()));
					builder.Append("):");
					builder.Append(method.ReturnType.GetFullName());
					yield return new KeyValuePair<string, bool>(builder.ToString(), isPublic);
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
					yield return new KeyValuePair<string, bool>(builder.ToString(), isPublic);
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
					yield return new KeyValuePair<string, bool>(builder.ToString(), isPublic);
					break;
				}
			}
		}
	}
}

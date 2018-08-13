using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace AssemblyComparer {
	public static class Extensions {
		public static IEnumerable<T> InstantiateAttributes<T>(this IEnumerable<CustomAttributeData> that)
				where T: Attribute {
			return that
					.Where(a => a.AttributeType == typeof(T))
					.Select(a => {
						var attribute = (T)a.Constructor.Invoke(a.ConstructorArguments.Select(p => p.Value).ToArray());
						if (a.NamedArguments != null) {
							foreach (var namedArgument in a.NamedArguments) {
								if (namedArgument.IsField) {
									((FieldInfo)namedArgument.MemberInfo).SetValue(attribute, namedArgument.TypedValue.Value);
								} else {
									((PropertyInfo)namedArgument.MemberInfo).SetValue(attribute, namedArgument.TypedValue.Value);
								}
							}
						}
						return attribute;
					});
		}

		public static bool IsPublic(this ObfuscationAttribute attribute, bool isPublicDefault) {
			if (attribute != null) {
				return attribute.Exclude;
			}
			return isPublicDefault;
		}

		public static string Replace(this IReadOnlyDictionary<string, string> keyValuePairs, string str) {
			return Regex.Replace(str, @"\$(\w+)\$", m => keyValuePairs[m.Groups[1].Value]);
		}

		public static string GetFullName(this Type type) {
			if (type.IsGenericParameter) {
				return type.Name;
			}
			var result = new StringBuilder();
			result.Append(type.Namespace);
			result.Append(".");
			result.Append(type.Name);
			if (type.IsGenericTypeDefinition || type.IsGenericType) {
				result.Append("[[");
				result.Append(string.Join(",", type.GetGenericArguments().Select(GetFullName)));
				result.Append("]]");
			}
			return result.ToString();
		}
	}
}

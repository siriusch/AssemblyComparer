using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using System.Xml.Linq;

namespace AssemblyComparer {
	public class AssemblyAnalyzer: MarshalByRefObject {
		private static XAttribute RenderTypeName(Type type) {
			return type == null ? null : new XAttribute("type", type.FullName ?? type.Namespace+"."+type.Name);
		}

		private static XElement RenderInheritsType(Type type) {
			return type == null ? null : new XElement("inherits", RenderTypeName(type));
		}

		private static XElement RenderImplementsType(Type type) {
			return new XElement("implements", RenderTypeName(type));
		}

		private static XAttribute RenderValue(object obj) {
			if (obj == null) {
				obj = "null";
			} else if (obj.GetType().IsPrimitive && !(obj is string) && !(obj is char)) {
				obj = Convert.ToString(obj, CultureInfo.InvariantCulture);
			} else {
				var str = HttpUtility.JavaScriptStringEncode(obj is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : obj.ToString(), true);
				obj = str.Replace("\uFFFE", @"\uFFFE").Replace("\uFFFF", @"\uFFFF");
			}
			return new XAttribute("value", obj);
		}

		private static XAttribute RenderName(string name) {
			return new XAttribute("name", name);
		}

		private static XAttribute RenderNamespace(string @namespace) {
			return new XAttribute("namespace", @namespace);
		}

		private static XElement RenderAttribute(CustomAttributeData attributeData) {
			return new XElement("attribute",
					RenderTypeName(attributeData.AttributeType),
					attributeData
							.Constructor
							.GetParameters()
							.Select(p =>
									new XElement("parameter",
											RenderTypeName(p.ParameterType),
											RenderValue(attributeData.ConstructorArguments[p.Position].Value))),
					attributeData.NamedArguments.Select(na => new XElement(na.IsField ? "field" : "property",
							RenderName(na.MemberInfo.Name),
							RenderTypeName(na.TypedValue.ArgumentType),
							RenderValue(na.TypedValue.Value))));
		}

		private static XElement RenderType(Type type) {
			var isDelegate = typeof(Delegate).IsAssignableFrom(type);
			return new XElement("type",
					RenderTypeName(type),
					RenderName(type.Name),
					RenderNamespace(type.Namespace),
					new XAttribute("kind", isDelegate
							? "delegate"
							: type.IsEnum
									? "enum"
									: type.IsValueType
											? "struct"
											: type.IsInterface
													? "interface"
													: "class"),
					type.GetCustomAttributesData().Select(RenderAttribute),
					RenderInheritsType(type.BaseType),
					type.GetInterfaces().Select(RenderImplementsType),
					type.GetMembers(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.DeclaredOnly).Where(m => !(m is Type)).Select(RenderMember));
		}

		private static XElement RenderMember(MemberInfo member) {
			switch (member) {
			case FieldInfo field:
				return RenderField(field);
			case PropertyInfo property:
				return RenderProperty(property);
			case MethodBase method:
				return RenderMethodBase(method);
			case EventInfo @event:
				return RenderEvent(@event);
			}
			throw new NotImplementedException();
		}

		private static XElement RenderEvent(EventInfo @event) {
			return new XElement("event",
					RenderName(@event.Name),
					RenderTypeName(@event.EventHandlerType),
					@event.GetCustomAttributesData().Select(RenderAttribute));
		}

		private static XElement RenderMethodBase(MethodBase method) {
			return new XElement(method.IsConstructor ? "constructor" : "method",
					new XAttribute("static", method.IsStatic),
					RenderName(method.Name),
					RenderTypeName((method as MethodInfo)?.ReturnType),
					method.GetParameters().Select(RenderParameter),
					method.GetCustomAttributesData().Select(RenderAttribute));
		}

		private static XElement RenderParameter(ParameterInfo parameter) {
			return new XElement("parameter",
					RenderName(parameter.Name),
					RenderTypeName(parameter.ParameterType),
					new XAttribute("position", parameter.Position),
					parameter.GetCustomAttributesData().Select(RenderAttribute));
		}

		private static XElement RenderField(FieldInfo field) {
			if ((field.DeclaringType?.IsEnum).GetValueOrDefault() && !field.IsStatic) {
				return null;
			}
			return new XElement("field",
					new XAttribute("static", field.IsStatic),
					new XAttribute("readonly", field.IsInitOnly),
					new XAttribute("literal", field.IsLiteral),
					RenderName(field.Name),
					RenderTypeName(field.FieldType),
					field.IsLiteral ? RenderValue(field.GetRawConstantValue()) : null,
					field.GetCustomAttributesData().Select(RenderAttribute));
		}

		private static XElement RenderProperty(PropertyInfo property) {
			var accessors = property.GetAccessors();
			return new XElement("property",
					RenderName(property.Name),
					RenderTypeName(property.PropertyType),
					new XAttribute("static", accessors.First().IsStatic),
					property.GetCustomAttributesData().Select(RenderAttribute),
					accessors.Select(RenderMethodBase));
		}

		public string Analyze(string assemblyFile) {
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
			var document = new XDocument(
					new XElement("assembly", 
							new XAttribute("name", assembly.FullName),
							assembly.GetCustomAttributesData().Select(RenderAttribute),
							assembly.GetExportedTypes().Select(RenderType)));
			return document.ToString(SaveOptions.None);
		}
	}
}
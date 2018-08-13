using System;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace AssemblyComparer {
	public class Program {
		public static string[] AnalyzeAssembly(string dllName) {
			AppDomain domain = AppDomain.CreateDomain("AssemblyAnalyzer");
			try {
				var anaylzer = (AssemblyAnalyzer)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().CodeBase, typeof(AssemblyAnalyzer).FullName);
				return anaylzer.Analyze(dllName);
			} finally {
				AppDomain.Unload(domain);
			}
		}

		static void Main(string[] args) {
			var dllNew = @"C:\Data\hg\Sirius.Common\src\Sirius.Common\bin\Debug\Sirius.Common.dll";
			var dllOld = @"C:\Data\hg\Sirius.Web\Sirius.Web\bin\Debug\Sirius.Common.dll";
			Console.WriteLine(string.Join(Environment.NewLine, AnalyzeAssembly(dllNew)));
			AnalyzeAssembly(dllOld);
			if (Debugger.IsAttached) {
				Console.ReadKey();
			}
		}
	}
}

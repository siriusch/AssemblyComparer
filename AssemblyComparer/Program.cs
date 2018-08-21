using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using CommandLine;

using NuGet;

namespace AssemblyComparer {
	public class Program {
		private static readonly Regex rxVersion = new Regex(@"(?<=(?<!//[^\r\n]*)\[\s*assembly\s*:\s*Assembly(File|Informational)?Version\s*\(\s*"")[^""]+(?=""\s*\)\s*\])", RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);

		public static KeyValuePair<string, bool>[] AnalyzeAssembly(string dllName, bool checkInheritance) {
			var domain = AppDomain.CreateDomain("AssemblyAnalyzer");
			try {
				var anaylzer = (AssemblyAnalyzer)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().CodeBase, typeof(AssemblyAnalyzer).FullName, false, BindingFlags.Default, null, new object[] { checkInheritance }, CultureInfo.InvariantCulture, null);
				return anaylzer.Analyze(dllName);
			} finally {
				AppDomain.Unload(domain);
			}
		}

		public static KeyValuePair<string, bool>[] AnalyzeAssembly(byte[] dllData, string loadPath, bool checkInheritance) {
			var domain = AppDomain.CreateDomain("AssemblyAnalyzer");
			try {
				var anaylzer = (AssemblyAnalyzer)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().CodeBase, typeof(AssemblyAnalyzer).FullName, false, BindingFlags.Default, null, new object[] {checkInheritance}, CultureInfo.InvariantCulture, null);
				return anaylzer.Analyze(dllData, loadPath);
			} finally {
				AppDomain.Unload(domain);
			}
		}

		static void Main(string[] args) {
			Parser.Default
					.ParseArguments<Options>(args)
					.WithParsed(options => {
						var nuspecFile = options.GetNuSpecFile();
						var filter = options.GetFileFilter();
						var nuspec = XDocument.Load(nuspecFile.FullName);
						var packageId = nuspec
								.Element("package")
								.Element("metadata")
								.Element("id")
								.Value;
						Console.WriteLine("Package " + packageId);
						var package = options
								.GetNuGetFeedUrls()
								.Select(PackageRepositoryFactory.Default.CreateRepository)
								.SelectMany(repo => repo.FindPackagesById(packageId)).First(item => item.IsReleaseVersion() && item.IsLatestVersion);
						Console.WriteLine("Version " + package.Version);
						var properties = options.GetProperties();
						var files = package
								.GetFiles()
								.ToDictionary(f => f.Path.Replace('/', '\\'));
						var basePath = options.GetbasePath();
						var added = new List<string>();
						var removed = new List<string>();
						var ignored = new List<string>();
						foreach (var file in nuspec
								.Element("package")
								.Element("files")
								.Elements("file")
								.Select(e => new KeyValuePair<string, string>(Path.Combine(e.Attribute("target").Value.Replace('/', '\\'), Path.GetFileName(e.Attribute("src").Value)), Path.Combine(basePath, properties.Replace(e.Attribute("src").Value).Replace('/', '\\'))))
								.Where(pair => filter(pair.Key))) {
							Console.WriteLine(file.Key + " => " + file.Value);
							var allOld = new HashSet<string>(StringComparer.Ordinal);
							var ignore = new HashSet<string>(StringComparer.Ordinal);
							if (files.TryGetValue(file.Key, out var packageFile)) {
								using (var stream = packageFile.GetStream()) {
									AnalyzeAssembly(stream.ReadAllBytes(), Path.GetDirectoryName(file.Value), options.CheckInheritance).AddAllDistributed(allOld, ignore);
								}
							}
							var allNew = new HashSet<string>(StringComparer.Ordinal);
							AnalyzeAssembly(file.Value, options.CheckInheritance).AddAllDistributed(allNew, ignore);
							Console.WriteLine("Assembly Stats: Old={0}, New={1}", allOld.Count, allNew.Count);
							added.AddRange(allNew.Except(allOld).Except(ignore));
							removed.AddRange(allOld.Except(allNew).Except(ignore));
							ignored.AddRange(ignore);
						}
						Console.WriteLine("*** ADDED ***");
						Console.WriteLine(string.Join(Environment.NewLine, added));
						Console.WriteLine("*** REMOVED ***");
						Console.WriteLine(string.Join(Environment.NewLine, removed));
						Console.WriteLine("*** IGNORED ***");
						Console.WriteLine(string.Join(Environment.NewLine, ignored));
						var version = package.Version.Version;
						if (removed.Any()) {
							version = new Version(version.Major + 1, 0, 0);
						} else if (added.Any()) {
							version = new Version(version.Major, version.Minor + 1, 0);
						} else {
							version = new Version(version.Major, version.Minor, version.Build + 1);
						}
						var semVer = new SemanticVersion(version, package.Version.SpecialVersion, package.Version.Metadata);
						Console.WriteLine("New version: " + semVer);
						if (!options.DryRun) {
							// ReSharper disable once StringLiteralTypo
							Console.WriteLine($"##teamcity[buildNumber '{semVer}']");
							nuspec.Element("package").Element("metadata").Element("version").Value = semVer.ToString();
							nuspec.Save(nuspecFile.FullName);
							foreach (var assemblyInfo in Directory.EnumerateFiles(basePath, "AssemblyInfo.cs", SearchOption.AllDirectories)) {
								using (var stream = File.Open(assemblyInfo, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
									string content;
									Encoding encoding;
									using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true)) {
										content = reader.ReadToEnd();
										encoding = reader.CurrentEncoding;
									}
									var newContent = rxVersion.Replace(content, semVer.Version.ToString());
									if (StringComparer.InvariantCulture.Equals(content, newContent)) {
										// No change
										continue;
									}
									stream.Seek(0, SeekOrigin.Begin);
									using (var writer = new StreamWriter(stream, encoding, 1024, true)) {
										writer.Write(newContent);
									}
									stream.SetLength(stream.Position);
									Console.WriteLine("Patched versions in " + assemblyInfo);
								}
							}
						}
					});
			if (Debugger.IsAttached) {
				Console.ReadKey();
			}
		}
	}
}

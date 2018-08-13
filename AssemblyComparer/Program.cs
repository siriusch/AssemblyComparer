using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

using CommandLine;

using NuGet;

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

		public static string[] AnalyzeAssembly(byte[] dllData, string loadPath) {
			AppDomain domain = AppDomain.CreateDomain("AssemblyAnalyzer");
			try {
				var anaylzer = (AssemblyAnalyzer)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().CodeBase, typeof(AssemblyAnalyzer).FullName);
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
						foreach (var file in nuspec
								.Element("package")
								.Element("files")
								.Elements("file")
								.Select(e => new KeyValuePair<string, string>(Path.Combine(e.Attribute("target").Value.Replace('/', '\\'), Path.GetFileName(e.Attribute("src").Value)), Path.Combine(basePath, properties.Replace(e.Attribute("src").Value).Replace('/', '\\'))))
								.Where(pair => filter(pair.Key))) {
							Console.WriteLine(file.Key + " => " + file.Value);
							var allOld = new HashSet<string>(StringComparer.Ordinal);
							IPackageFile packageFile;
							if (files.TryGetValue(file.Key, out packageFile)) {
								using (var stream = packageFile.GetStream()) {
									allOld.UnionWith(AnalyzeAssembly(stream.ReadAllBytes(), Path.GetDirectoryName(file.Value)));
								}
							}
							var allNew = new HashSet<string>(AnalyzeAssembly(file.Value), StringComparer.Ordinal);
							Console.WriteLine("Assembly Stats: Old={0}, New={1}", allOld.Count, allNew.Count);
							added.AddRange(allNew.Except(allOld));
							removed.AddRange(allOld.Except(allNew));
						}
						Console.WriteLine("*** ADDED ***");
						Console.WriteLine(string.Join(Environment.NewLine, added));
						Console.WriteLine("*** REMOVED ***");
						Console.WriteLine(string.Join(Environment.NewLine, removed));
						var version = package.Version.Version;
						if (removed.Any()) {
							version = new Version(version.Major + 1, version.Minor, version.Build);
						}
						else if (added.Any()) {
							version = new Version(version.Major, version.Minor + 1, version.Build);
						}
						else {
							version = new Version(version.Major, version.Minor, version.Build + 1);
						}
						SemanticVersion semVer = new SemanticVersion(version, package.Version.SpecialVersion, package.Version.Metadata);
						Console.WriteLine("New version: "+semVer);
						if (!options.DryRun) {
							// ReSharper disable once StringLiteralTypo
							Console.WriteLine($"##teamcity[buildNumber '{semVer}'])");
							nuspec.Element("package").Element("metadata").Element("version").Value = semVer.ToString();
							nuspec.Save(nuspecFile.FullName);
						}

					});
			if (Debugger.IsAttached) {
				Console.ReadKey();
			}
		}
	}
}

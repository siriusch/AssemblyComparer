using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using CommandLine;

namespace AssemblyComparer {
	internal class Options {
		[Option("Source", HelpText = "NuGet package source to get the baseline DLL.", Min = 1)]
		public IEnumerable<string> NuGetFeedUrls {
			get;
			set;
		}

		[Option('p', "Properties", HelpText = "Properties to use in nuspec file.", Min = 1)]
		public IEnumerable<string> Properties {
			get;
			set;
		}

		[Option("Files", HelpText = "Filenames as specified in NuSpec file to use.", Min = 1)]
		public IEnumerable<string> Files {
			get;
			set;
		}

		[Option("DryRun", Default = false, HelpText = "Runs without making any modification.")]
		public bool DryRun {
			get;
			set;
		}

		[Option(HelpText = "The base path of the files defined in the nuspec file.")]
		public string BasePath {
			get;
			set;
		}

		[Value(0, Required = true, MetaName = "nuspec", HelpText = "nuspec file.")]
		public string NuSpec {
			get;
			set;
		}

		public FileInfo GetNuSpecFile() {
			var result = new FileInfo(this.NuSpec);
			if (!result.Exists) {
				throw new FileNotFoundException("nuspec file not found", NuSpec);
			}
			return result;
		}

		public string GetbasePath() {
			return string.IsNullOrEmpty(BasePath) ? GetNuSpecFile().DirectoryName : BasePath;
		}

		public Predicate<string> GetFileFilter() {
			if (Files == null || !Files.Any()) {
				return name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
			}
			var rx = new Regex("^(" + string.Join("|",
									  Files.Select(f => Regex.Replace(f, @"\*+|\?|(?<sep>[\\\/]+)|[^\\\/\*\?]+",
											  match => match.Groups["sep"].Success
													  ? @"\\"
													  : match.Value == "*"
															  ? "[^/\\]*"
															  : match.Value.StartsWith("*", StringComparison.Ordinal)
																	  ? ".*"
																	  : match.Value.StartsWith("?", StringComparison.Ordinal)
																			  ? @"[^/\\]"
																			  : Regex.Escape(match.Value)))) + ")$",
					RegexOptions.IgnoreCase|RegexOptions.ExplicitCapture|RegexOptions.CultureInvariant);
			return rx.IsMatch;
		}

		public IEnumerable<string> GetNuGetFeedUrls() {
			if (NuGetFeedUrls == null || !NuGetFeedUrls.Any()) {
				return new[] { "https://www.nuget.org/api/v2/" };
			}
			return NuGetFeedUrls;
		}

		public IReadOnlyDictionary<string, string> GetProperties() {
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (Properties != null) {
				foreach (var property in Properties) {
					var split = property.Split(new[] {'='}, 2);
					result.Add(split[0].Trim(), split[1]);
				}
			}
			return result;
		}
	}
}

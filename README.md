<!-- GRAPHIC -->

# Sirius AssemblyComparer

Tool for comparing .NET assembly files against a NuGet package in order to determine next SemVer version to use for the package.

<!-- badges -->

---
## Description

For our librares we use (and partially publish) NuGet packages. In order to follow [Semantic Versioning](https://semver.org/) guidelines correctly we wanted to have tool support for discovering changes to the binary API of the assembly being built and published.

Given an assembly, the tool can create a list of its public parts (types and members) which reflects (no pun intended) the public API of the assembly.

Given this list created for two assembly versions, the difference between these can tell whether the public API is unchanged, changed but non-breaking (members or types were added), or breaking (members or types were removed).

---
## Process

- The AssemblyComparer loads the given .nuspec file and uses it to determine the package ID.
- The latest version of the package is retrieved from NuGet, so that the currently released assembly files and the package version are known.
- The `file` tags in the .nuspec file are used to determine which assemblies to take into account.
- For each assembly, a public part list is created of both the version in the package as well as the version on disk (locating the files on disk like `nuget pack` would do).
- After comparing all files matched by the filter, the old and new part lists are compared:
    - If parts were *removed* (breaking binary API change), increase the major version and reset minor and patch version numbers.
    - If parts were *added* (non-breaking binary API change), increase the minor version and reset the patch version number.
    - If the parts are identical, increase the patch version.
- Apply the new version in TeamCity through [Build Script Interaction](https://confluence.jetbrains.com/display/TCD18/Build+Script+Interaction+with+TeamCity): `##teamcity[buildNumber '1.2.3']`
- Patch the .nuspec file with the new version. 
- Patch any `AssemblyVersion`, `AssemblyFileVersion` and `AssemblyInformationalVersion` attributes found in `AssemblyInfo.cs` files under the `basepath`.

**Disclaimer:** The tool is specific to our use case so your mileage may vary.

---
## Public types and members exclusion

Sometimes some members are public but not intended to be part of the public API. Therefore, these should be able to be changed without being detected as a API change.

Coincidentally, obfuscation tools face the same challenge: they need to know which types and members may be obfuscated (public but private-use) and which have to remain unchanged (public API). Therefore, the AssemblyComparer tool takes the settings specified in the `ObfuscateAssemblyAttribute` and `ObfuscationAttribute` into account.

Additionally, any type or method with a `[Conditional("DEBUG")]` attribute will be ignored as well. This allows to use debug assemblies during version comparison against release NuGet packages.  

---
## Integration into build process

We roughly have the following build steps in place to integrate AssemblyComparer:
- Restore packages
- Create Debug Build
- Run tests
- *Run AssemblyComparer*
- Create Release Build (now with the newly determined version)
- Create NuGet package

---
## Command Line Options

| Argument   | Description |
| - | - |
| `file.nuspec` | *Required.* .nuspec file path (relative to the current directory). |
| `--Source ...` | *Optional.* Accepts one (or more) NuGet package feeds to use as source for the baseline version of the NuGet package containing the assemblies. If not specified, the [nuget.org](https://nuget.org) feed will be used. |
| `-p ...`, `--Properties ...` | *Optional.* Accepts `key=value` pairs of properties. This can be used for filling placeholders in the paths of the nuspec file, e.g. `bin\$Configuration$\some.dll` and a `-p Configuration=Debug`, the resulting path would be `bin\Debug\some.dll`. *Note:* missing properties will cause a runtime exception. |
| `--Files ...` | *Optional.* File patterns matched against the `src` attribute of the .nuspec `file` elements. If not specified, all files with the `.dll` extension will be matched. |
| `--DryRun` | *Optional.* If specified, the analysis is output to the console but no changes are made. |
| `-i`, `--CheckInheritance` | *Optional.* Controls whether the inheritance is seen as relevant or not during analysis. If enabled, the base type must match and only declared members are checked. If a base type is introduced and some members are moved to it, having the option enabled will cause a major version increment due to removed members on the type being analyzed, whereas without inheritance check only a minor version increment is done since the effective public API remains the same. |
| `--BasePath ...` | *Optional.* The base path to use. By default it uses path where the .nuspec file is located. |

---
## Source

[https://github.com/siriusch/AssemblyComparer](https://github.com/siriusch/AssemblyComparer)

---
## License

- **[MIT license](LICENSE.txt)**
- Copyright 2018 © <a href="https://www.sirius.ch" target="_blank">Sirius Technologies AG</a>.

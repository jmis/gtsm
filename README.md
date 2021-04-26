A command line tool to generate TypeScript models from C# models.

## Config File
Types referenced by models will be recursively included unless explicitly excluded or the type is not found in the listed assemblies.

TypeIncludes and TypeExcludes are regular expressions evaluated against the types full name (namespace plus type name).

```
{
	"OutputFiles": [{
		"File": "./client/src/server.types.ts",
		"Assemblies": [
			"./server/bin/Debug/net5.0/App.dll",
			"./server/bin/Debug/net5.0/App.Dep.dll"
		],
		"TypeIncludes": [
			"App.ViewModels.*ViewModel$",
			"App.Dep.*Model$"
		],
		"TypeExcludes": [
			"App.ViewModels.NotUsedViewModel"
		]
	}],
	"Options": {
		"CamelCaseProperties": true
	}
}
```

## Usage
```
gtsm -c <path to config file>
```

## Build
Produces a platform-specific single file that does not require dotnet core to be installed.
```
dotnet publish -o ./out/ -r <platform> -c Publish
```
Example platforms: linux-x64, win-x64
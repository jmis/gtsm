A command line tool to generate TypeScript models from C# models.  GTSM does not require you to add anything to your code.  C# types are found via regular expressions specified in a config file.

## Features
- Recursively generate models for types automatically
- Use regular expressions to include and exclude types
- Generate multiple files
  - Import statements created between generated files
- Scan multiple assemblies
- Output interfaces or classes
  - Classes generate with initialization constructors

## Config File
Types referenced by models will be recursively included unless explicitly excluded or the type is not found in the listed assemblies.

TypeIncludes and TypeExcludes are regular expressions evaluated against the types full name (namespace plus type name).

All paths are relative to the config file.

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
		],
		"OutputType": "interface"
	}, {
		"File": "./client/src/server.actions.ts",
		"Assemblies": [
			"./server/bin/Debug/net5.0/App.dll",
			"./server/bin/Debug/net5.0/App.Dep.dll"
		],
		"TypeIncludes": [
			"App.*Actions$"
		],
		"TypeExcludes": [],
		"OutputType": "class"
	}],
	"Options": {
		"CamelCaseProperties": true
	}
}
```

## Run
```
dotnet run -- -c <path to config file>
```

## Publish
Produces a platform-specific single file that does not require dotnet core to be installed.
```
dotnet publish -o ./out/ -r <platform> -c Publish
```
Example platforms: linux-x64, win-x64
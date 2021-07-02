using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using Microsoft.Extensions.Configuration;

namespace GenerateTSModelsFromCS
{
	public record OutputFile
	{
		public string File { get; set; } = "";
		public string[] Assemblies { get; set; } = Array.Empty<string>();
		public string[] TypeIncludes { get; set; } = Array.Empty<string>();
		public string[] TypeExcludes { get; set; } = Array.Empty<string>();
		public string[] Imports { get; set; } = Array.Empty<string>();
		public string OutputType { get; set; } = "interface";
	}

	public record Options
	{
		public bool CamelCaseProperties { get; set; }
	}

	public record GenerateTSModelsFromCSConfiguration
	{
		public OutputFile[] OutputFiles { get; set; }
		public Options Options { get; set; }
	}

	public class Program
	{
		public static bool IsNumber(string typeName) => Regex.IsMatch(typeName, "Int32|Int64|Double|DateTime");
		public static bool IsString(string typeName) => Regex.IsMatch(typeName, "(Char|String)$");
		public static bool IsBoolean(string typeName) => Regex.IsMatch(typeName, "Boolean");
		public static bool IsArrayLike(string typeName) => Regex.IsMatch(typeName, "^ImmutableList|List");
		public static bool IsDictionaryLike(string typeName) => Regex.IsMatch(typeName, "^Dictionary|ImmutableDictionary");
		public static bool IsNullable(string typeName) => Regex.IsMatch(typeName, "^Nullable");

		public static string ConvertTypeToTypescriptType(string typeName) =>
				IsNumber(typeName) ? "number"
				: IsString(typeName) ? "string"
				: IsBoolean(typeName) ? "boolean"
				: typeName;

		public static string ProcessTypeSig(TypeSig propertyType, Queue<TypeSig> typesToFind)
		{
			if (propertyType.IsSZArray)
			{
				return $"{ProcessTypeSig(propertyType.ToSZArraySig().ScopeType.ToTypeSig(), typesToFind)}[]";
			}
			else if (IsArrayLike(propertyType.TypeName))
			{
				return $"{ProcessTypeSig(propertyType.ToGenericInstSig().GenericArguments[0], typesToFind)}[]";
			}
			else if (IsDictionaryLike(propertyType.TypeName))
			{
				return $"Record<{ProcessTypeSig(propertyType.ToGenericInstSig().GenericArguments[0], typesToFind)}, {ProcessTypeSig(propertyType.ToGenericInstSig().GenericArguments[1], typesToFind)}>";
			}
			else if (propertyType.IsGenericInstanceType)
			{
				return $"{propertyType.TypeName.Replace("`1", "")}<{ProcessTypeSig(propertyType.ToGenericInstSig().GenericArguments[0], typesToFind)}>";
			}

			typesToFind.Enqueue(propertyType);
			return $"{ConvertTypeToTypescriptType(propertyType.TypeName)}";
		}

		public static string ProcessEnum(TypeDef enumType)
		{
			if (enumType.GetEnumUnderlyingType().FullName.Contains("Int32"))
			{
				return new StringBuilder()
					.Append("export enum ")
					.Append(enumType.Name)
					.AppendLine(" {")
					.Append('\t')
					.AppendLine(string.Join(",\r\n\t", enumType.Fields.Where(f => f.IsLiteral).Select(f => $"{f.Name} = {f.Constant.Value}").ToList()))
					.AppendLine("}")
					.ToString();
			}

			return new StringBuilder()
				.Append("export enum ")
				.Append(enumType.Name)
				.AppendLine(" {")
				.Append('\t')
				.AppendLine(string.Join(",\r\n\t", enumType.Fields.Where(f => f.IsLiteral).Select(f => $"{f.Name} = \"{f.Name.ToCamelCase()}\"").ToList()))
				.AppendLine("}")
				.ToString();
		}

		public static string ProcessClass(TypeDef classType, Queue<TypeSig> typesToFind, Options options, OutputFile outputFile)
		{
			var output = new StringBuilder();
			output.Append($"export {outputFile.OutputType} ");

			var className = classType.Name;

			if (classType.GenericParameters.Count > 0)
			{
				className = classType.Name.Substring(0, classType.Name.Length - 2);
				className += $"<{string.Join(',', classType.GenericParameters.Select(p => p.Name))}>";
			}

			output.Append(className);

			if (classType.BaseType != null && classType.BaseType.Name != "Object")
			{
				output.Append(" extends ").Append(ProcessTypeSig(classType.BaseType.ToTypeSig(), typesToFind));
			}

			output.AppendLine(" {");

			classType.Properties.Where(p => p.PropertySig.RetType.TypeName != "Type").ToList().ForEach(p =>
			{
				var propertyName = p.Name.ToString();
				propertyName = options.CamelCaseProperties ? propertyName.ToCamelCase() : propertyName;

				var propertyType = p.PropertySig.RetType;
				var isNullableString = "";

				if (IsNullable(propertyType.TypeName))
				{
					isNullableString = "?";
					propertyType = propertyType.ToGenericInstSig().GenericArguments[0];
				}

				output.AppendLine($"\t{propertyName}{isNullableString}: {ProcessTypeSig(propertyType, typesToFind)};");
			});

			if (outputFile.OutputType == "class")
			{
				output.AppendLine($"\tpublic constructor(init?: Partial<{className}>) {{ Object.assign(this, init); }}");
			}

			return output.AppendLine("}").ToString();
		}

		public static string CreateStubClass(string className, OutputFile outputFile)
		{
			var output = new StringBuilder();
			output.Append($"export {outputFile.OutputType} ");
			output.Append(className);
			output.AppendLine(" {");
			return output.AppendLine("}").ToString();
		}

		public static void ProcessOutputFile(OutputFile outputFileConfig, Options options, List<Tuple<string, TypeSig>> typesFoundInOtherFiles)
		{
			var fileContent = new StringBuilder();
			var modCtx = ModuleDef.CreateModuleContext();
			var loadedAssemblies = outputFileConfig.Assemblies.Select(p => ModuleDefMD.Load(p, modCtx)).ToList();
			var warnings = new StringBuilder();
			var typesToFind = new Queue<TypeSig>();
			var typesFoundForFile = new List<TypeSig>();
			var typesToImport = new List<Tuple<string, TypeSig>>();

			var allDefinedTypes = loadedAssemblies
				.SelectMany(a => a.GetTypes())
				.Where(t => t.IsPublic)
				.ToDictionary(t => t.FullName, t => t);

			allDefinedTypes.Values
				.Where(t => outputFileConfig.TypeIncludes.Any(ti => Regex.IsMatch(t.FullName, ti)))
				.Where(t => outputFileConfig.TypeExcludes.All(te => !Regex.IsMatch(t.FullName, te)))
				.ToList()
				.ForEach(t => typesToFind.Enqueue(t.ToTypeSig()));

			typesToFind.OrderBy(x => x.FullName).ToList().ForEach(x => Console.WriteLine(x.FullName));

			while (typesToFind.Any())
			{
				var currentTypeSig = typesToFind.Dequeue();

				if (typesFoundInOtherFiles.Any(f => f.Item2.FullName == currentTypeSig.FullName))
				{
					var match = typesFoundInOtherFiles.First(f => f.Item2.FullName == currentTypeSig.FullName);
					if (typesToImport.All(t => t.Item2.FullName != match.Item2.FullName)) typesToImport.Add(match);
					continue;
				}

				if (typesFoundForFile.Any(f => f.FullName == currentTypeSig.FullName))
				{
					continue;
				}

				if (outputFileConfig.TypeExcludes.Any(ti => Regex.IsMatch(currentTypeSig.FullName, ti)))
				{
					fileContent.AppendLine(CreateStubClass(allDefinedTypes[currentTypeSig.FullName].Name, outputFileConfig));
					continue;
				}

				if (!allDefinedTypes.ContainsKey(currentTypeSig.FullName))
				{
					typesFoundForFile.Add(currentTypeSig);
					if (!currentTypeSig.IsPrimitive && !currentTypeSig.IsCorLibType && currentTypeSig.FullName != "System.DateTime") warnings.AppendLine("Can't find type " + currentTypeSig.FullName);
					continue;
				}

				var currentType = allDefinedTypes[currentTypeSig.FullName];
				typesFoundForFile.Add(currentTypeSig);
				var output = currentType.IsEnum ? ProcessEnum(currentType) : ProcessClass(currentType, typesToFind, options, outputFileConfig);
				fileContent.AppendLine(output);
			}

			var imports = new StringBuilder();
			typesToImport.GroupBy(f => f.Item1).ToList().ForEach(g =>
			{
				var fileToImport = new Uri(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, g.Key)));
				var currentFile = new Uri(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, outputFileConfig.File)));
				var importPath = currentFile.MakeRelativeUri(fileToImport).ToString();
				importPath = Path.Combine(Path.GetDirectoryName(importPath), Path.GetFileNameWithoutExtension(importPath));
				if (!importPath.StartsWith(".")) importPath = "./" + importPath;
				var typeNames = g.ToList().Select(t => t.Item2).Where(t => !t.IsPrimitive && !t.IsCorLibType && t.FullName != "System.DateTime").Select(t => t.TypeName).ToList();
				imports.Append("import { ").Append(string.Join(", ", typeNames)).AppendLine($" }} from \"{importPath}\";");
			});

			imports.AppendLine();
			imports.Append(fileContent);

			typesFoundForFile.ForEach(f => typesFoundInOtherFiles.Add(Tuple.Create(outputFileConfig.File, f)));

			File.WriteAllText(outputFileConfig.File, imports.ToString().Trim());
			Console.WriteLine(warnings.ToString());
		}

		public static void Main(string[] args)
		{
			var rootCommand = new RootCommand
			{
				new Option<string>(new[] { "--config", "-c" })
				{
					Description = "Config file path",
					IsRequired = true,
					ArgumentHelpName = "path"
				}
			};

			rootCommand.Name = "gtsm";
			rootCommand.Description = "GenerateTSModelsFromCS - Generate TypeScript models from C# models";

			rootCommand.Handler = CommandHandler.Create<FileInfo>(config =>
			{
				Environment.CurrentDirectory = Path.GetDirectoryName(config.FullName);
				var configRoot = new ConfigurationBuilder().AddJsonFile(config.FullName).AddCommandLine(args).Build();
				var jsonConfiguration = configRoot.Get<GenerateTSModelsFromCSConfiguration>();
				var foundTypes = new List<Tuple<string, TypeSig>>();
				jsonConfiguration.OutputFiles.ToList().ForEach(file => ProcessOutputFile(file, jsonConfiguration.Options, foundTypes));
			});

			rootCommand.InvokeAsync(args).Wait();
		}
	}

	public static class GeneralExtensions
	{
		public static string ToCamelCase(this string s)
		{
			var firstChar = char.ToLower(s[0]);
			s = s.Remove(0, 1);
			s = firstChar + s;
			return s;
		}

		public static string ToCamelCase(this UTF8String encodedString)
		{
			return encodedString.ToString().ToCamelCase();
		}
	}
}

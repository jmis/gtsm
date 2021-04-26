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
			return new StringBuilder()
				.Append("export enum ")
				.Append(enumType.Name)
				.AppendLine(" {")
				.Append('\t')
				.AppendLine(string.Join(",\r\n\t", enumType.Fields.Where(f => f.IsLiteral).Select(f => f.Name).ToList()))
				.AppendLine("}")
				.ToString();
		}

		public static string ProcessClass(TypeDef classType, Queue<TypeSig> typesToFind, Options options)
		{
			var output = new StringBuilder();
			output.Append("export interface ");

			if (classType.GenericParameters.Count > 0)
			{
				output
					.Append(classType.Name.Substring(0, classType.Name.Length - 2))
					.Append('<')
					.Append(string.Join(',', classType.GenericParameters.Select(p => p.Name)))
					.Append('>');
			}
			else
			{
				output.Append(classType.Name);
			}

			if (classType.BaseType != null && classType.BaseType.Name != "Object")
			{
				output.Append(" extends ").Append(ProcessTypeSig(classType.BaseType.ToTypeSig(), typesToFind));
			}

			output.AppendLine(" {");

			classType.Properties.Where(p => p.PropertySig.RetType.TypeName != "Type").ToList().ForEach(p =>
			{
				var propertyName = p.Name.ToString();

				if (options.CamelCaseProperties)
				{
					var firstChar = char.ToLower(propertyName[0]);
					propertyName = propertyName.Remove(0, 1);
					propertyName = firstChar + propertyName;
				}

				var propertyType = p.PropertySig.RetType;
				var isNullableString = "";

				if (IsNullable(propertyType.TypeName))
				{
					isNullableString = "?";
					propertyType = propertyType.ToGenericInstSig().GenericArguments[0];
				}

				output.AppendLine($"\t{propertyName}{isNullableString}: {ProcessTypeSig(propertyType, typesToFind)};");
			});

			return output.AppendLine("}").ToString();
		}

		public static void ProcessOutputFile(OutputFile outputFileConfig, Options options)
		{
			var fileContent = new StringBuilder();
			var modCtx = ModuleDef.CreateModuleContext();
			var loadedAssemblies = outputFileConfig.Assemblies.Select(p => ModuleDefMD.Load(p, modCtx)).ToList();
			var warnings = new StringBuilder();
			var typesToFind = new Queue<TypeSig>();
			var foundTypes = new List<string>();

			var allDefinedTypes = loadedAssemblies
				.SelectMany(a => a.GetTypes())
				.Where(t => t.IsPublic)
				.ToDictionary(t => t.FullName, t => t);

			allDefinedTypes.Values
				.Where(t => outputFileConfig.TypeIncludes.Any(ti => Regex.IsMatch(t.FullName, ti)))
				.ToList()
				.ForEach(t => typesToFind.Enqueue(t.ToTypeSig()));

			while (typesToFind.Any())
			{
				var currentTypeSig = typesToFind.Dequeue();

				if (foundTypes.Contains(currentTypeSig.FullName)) continue;
				if (outputFileConfig.TypeExcludes.Any(ti => Regex.IsMatch(currentTypeSig.FullName, ti))) continue;

				if (!allDefinedTypes.ContainsKey(currentTypeSig.FullName))
				{
					foundTypes.Add(currentTypeSig.FullName);
					if (!currentTypeSig.IsPrimitive && !currentTypeSig.IsCorLibType && currentTypeSig.FullName != "System.DateTime") warnings.AppendLine("Can't find type " + currentTypeSig.FullName);
					continue;
				}

				var currentType = allDefinedTypes[currentTypeSig.FullName];
				foundTypes.Add(currentTypeSig.FullName);
				var output = currentType.IsEnum ? ProcessEnum(currentType) : ProcessClass(currentType, typesToFind, options);
				fileContent.AppendLine(output);
				Console.WriteLine(output);
			}

			File.WriteAllText(outputFileConfig.File, fileContent.ToString());
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
				var configRoot = new ConfigurationBuilder().AddJsonFile(config.FullName).AddCommandLine(args).Build();
				var jsonConfiguration = configRoot.Get<GenerateTSModelsFromCS.GenerateTSModelsFromCSConfiguration>();
				jsonConfiguration.OutputFiles.ToList().ForEach(file => ProcessOutputFile(file, jsonConfiguration.Options));
			});

			rootCommand.InvokeAsync(args).Wait();
		}
	}
}

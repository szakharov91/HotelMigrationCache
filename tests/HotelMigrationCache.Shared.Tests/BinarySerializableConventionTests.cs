using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HotelMigrationCache.Shared.Tests;

/// <summary>
/// Convention-тест: любой класс, реализующий <c>IBinarySerializable&lt;T&gt;</c>,
/// обязан быть объявлен как <c>partial</c> и помечен атрибутом <c>[GenerateBinarySerializerAttribute]</c>.
/// Иначе source generator не сможет дописать методы <c>SerializeToBinary</c>/<c>DeserializeFromBinary</c>,
/// либо, при ручной реализации без атрибута, будет утеряна единая точка контроля сериализации.
///
/// Проверка идёт по исходникам через Roslyn — так тест видит классы из всех проектов solution'а,
/// без необходимости добавлять на них ProjectReference.
/// </summary>
public sealed class BinarySerializableConventionTests
{
    private const string _interfaceName = "IBinarySerializable";
    private const string _attributeShortName = "GenerateBinarySerializer";
    private const string _attributeFullName = "GenerateBinarySerializerAttribute";

    [Fact]
    public void AllImplementers_MustBePartial_AndAnnotatedWithGenerateAttribute()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        Directory.Exists(srcDir).Should().BeTrue($"каталог src/ должен существовать под {repoRoot}");

        var implementers = FindImplementers(srcDir).ToList();

        implementers.Should().NotBeEmpty(
            "должен быть найден хотя бы один класс, реализующий IBinarySerializable<T>; " +
            "иначе тест ничего не проверяет — либо поиск сломан, либо интерфейс никто не использует");

        var violations = implementers
            .Where(c => !c.IsPartial || !c.HasAttribute)
            .Select(c => FormatViolation(c, repoRoot))
            .ToList();

        violations.Should().BeEmpty(
            "все классы, реализующие IBinarySerializable<T>, должны быть partial и помечены " +
            $"[GenerateBinarySerializerAttribute]. Нарушения:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<ImplementerInfo> FindImplementers(string srcDir)
    {
        var csFiles = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !PathContainsSegment(f, "obj") && !PathContainsSegment(f, "bin"))
            .Where(f => !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase));

        foreach (var file in csFiles)
        {
            var text = File.ReadAllText(file);
            if (text.IndexOf(_interfaceName, StringComparison.Ordinal) < 0)
                continue;

            var root = CSharpSyntaxTree.ParseText(text, path: file).GetRoot();

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!ImplementsBinarySerializable(classDecl))
                    continue;

                yield return new ImplementerInfo(
                    classDecl.Identifier.Text,
                    file,
                    IsPartial: classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
                    HasAttribute: HasGenerateBinarySerializerAttribute(classDecl));
            }
        }
    }

    private static bool ImplementsBinarySerializable(ClassDeclarationSyntax classDecl)
    {
        if (classDecl.BaseList is null)
            return false;

        foreach (var baseType in classDecl.BaseList.Types)
        {
            if (ExtractGenericInterfaceName(baseType.Type) is { } name &&
                name.Equals(_interfaceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractGenericInterfaceName(TypeSyntax type) => type switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText,
        QualifiedNameSyntax { Right: GenericNameSyntax qualifiedGeneric } => qualifiedGeneric.Identifier.ValueText,
        AliasQualifiedNameSyntax { Name: GenericNameSyntax aliasGeneric } => aliasGeneric.Identifier.ValueText,
        _ => null
    };

    private static bool HasGenerateBinarySerializerAttribute(ClassDeclarationSyntax classDecl)
    {
        foreach (var attributeList in classDecl.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = ExtractAttributeName(attribute.Name);
                if (name.Equals(_attributeShortName, StringComparison.Ordinal) ||
                    name.Equals(_attributeFullName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string ExtractAttributeName(NameSyntax name) => name switch
    {
        // e.g. `HotelMigrationCache.SourceGen.Attributes.GenerateBinarySerializer` -> `GenerateBinarySerializer`
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        // e.g. `GenerateBinarySerializer` or `@GenerateBinarySerializerAttribute` -> без ведущего `@`
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => name.ToString()
    };

    private static string FormatViolation(ImplementerInfo info, string repoRoot)
    {
        var problems = new List<string>(2);
        if (!info.IsPartial)
            problems.Add("не partial");
        if (!info.HasAttribute)
            problems.Add("нет [GenerateBinarySerializerAttribute]");

        var relative = Path.GetRelativePath(repoRoot, info.FilePath);
        return $"  - {info.ClassName} ({relative}): {string.Join(", ", problems)}";
    }

    private static bool PathContainsSegment(string path, string segment)
    {
        var wrapped = $"{Path.DirectorySeparatorChar}{segment}{Path.DirectorySeparatorChar}";
        return path.Contains(wrapped, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HotelMigrationCache.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Корень репозитория (файл HotelMigrationCache.slnx) не найден при подъёме от {AppContext.BaseDirectory}");
    }

    private sealed record ImplementerInfo(string ClassName, string FilePath, bool IsPartial, bool HasAttribute);
}

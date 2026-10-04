using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace Lokad.Qpdf.Tests;

public sealed class ApiDocumentationTests
{
    [Fact]
    public void EveryPublicMemberCarriesDocumentation()
    {
        Assembly assembly = typeof(Qpdf).Assembly;
        string? xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.True(File.Exists(xmlPath), "Expected API documentation next to the library assembly.");
        XDocument documentation = XDocument.Load(xmlPath);
        HashSet<string> documented = new(StringComparer.Ordinal);
        foreach (XElement member in documentation.Descendants("member"))
        {
            string? name = member.Attribute("name")?.Value;
            XElement? summary = member.Element("summary");
            if (name is not null && summary is not null && !string.IsNullOrWhiteSpace(summary.Value))
            {
                documented.Add(name);
            }
        }

        List<string> missing = new();
        foreach (Type type in assembly.GetExportedTypes())
        {
            Check(documented, missing, "T:" + TypeId(type));
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.IsSpecialName || IsCompilerGenerated(method))
                {
                    continue;
                }

                Check(documented, missing, "M:" + TypeId(type) + "." + MethodName(method) + MethodParameters(method));
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                Check(documented, missing, "P:" + TypeId(type) + "." + property.Name + IndexParameters(property));
            }

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsSpecialName || field.Name.Contains('<', StringComparison.Ordinal))
                {
                    continue;
                }

                Check(documented, missing, "F:" + TypeId(type) + "." + field.Name);
            }

            foreach (EventInfo raised in type.GetEvents(flags))
            {
                Check(documented, missing, "E:" + TypeId(type) + "." + raised.Name);
            }
        }

        Assert.True(missing.Count == 0, MissingMessage(missing));
    }

    private static void Check(HashSet<string> documented, List<string> missing, string identity)
    {
        if (!documented.Contains(identity))
        {
            missing.Add(identity);
        }
    }

    private static string MissingMessage(List<string> missing)
    {
        return "Undocumented public API members: " + string.Join("; ", missing);
    }

    private static string StripArity(string qualified)
    {
        int tick = qualified.LastIndexOf((char)96);
        if (tick >= 0 && tick + 1 < qualified.Length && char.IsDigit(qualified[tick + 1]))
        {
            return qualified.Substring(0, tick);
        }

        return qualified;
    }

    private static bool IsCompilerGenerated(MemberInfo member)
    {
        foreach (object attribute in member.GetCustomAttributes(false))
        {
            if (string.Equals(attribute.GetType().FullName, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string MethodName(MethodInfo method)
    {
        if (method.Name.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unhandled explicit interface member: " + method.Name);
        }

        if (method.IsGenericMethodDefinition)
        {
            return method.Name + "``" + method.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture);
        }

        return method.Name;
    }

    private static string MethodParameters(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return string.Empty;
        }

        return "(" + string.Join(",", parameters.Select(parameter => TypeId(parameter.ParameterType))) + ")";
    }

    private static string IndexParameters(PropertyInfo property)
    {
        ParameterInfo[] parameters = property.GetIndexParameters();
        if (parameters.Length == 0)
        {
            return string.Empty;
        }

        return "(" + string.Join(",", parameters.Select(parameter => TypeId(parameter.ParameterType))) + ")";
    }

    private static string TypeId(Type type)
    {
        if (type.IsGenericParameter)
        {
            string prefix = type.DeclaringMethod is null ? "`" : "``";
            return prefix + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
        }

        if (type.IsByRef)
        {
            return TypeId(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            return TypeId(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsPointer)
        {
            throw new InvalidOperationException("Unhandled pointer type: " + type.Name);
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            Type definition = type.GetGenericTypeDefinition();
            string qualified = definition.FullName ?? throw new InvalidOperationException("Anonymous constructed type: " + type.Name);
            return StripArity(qualified) + "{" + string.Join(",", type.GetGenericArguments().Select(TypeId)) + "}";
        }

        return type.FullName ?? throw new InvalidOperationException("Anonymous type: " + type.Name);
    }
}
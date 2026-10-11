using System.Reflection;
using System.Reflection.Emit;
using Lst.Modules.Shipments.Domain;

namespace Lst.UnitTests;

/// <summary>
/// U6 — Shipments Domain namespace'i EF Core, ASP.NET Core ve Npgsql'e bağımlı olmamalı.
/// Üye imzaları, öznitelikler ve metot gövdelerindeki IL token'ları (çağrılar, alan erişimleri, tip referansları) taranır.
/// </summary>
public sealed class ArchitectureTests
{
    private const string DomainNamespace = "Lst.Modules.Shipments.Domain";
    private static readonly string[] ForbiddenPrefixes = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"];

    private static readonly Dictionary<short, OpCode> OpCodeTable = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [Fact]
    public void U6_shipments_domain_does_not_reference_ef_core_aspnetcore_or_npgsql()
    {
        var domainTypes = typeof(Shipment).Assembly.GetTypes()
            .Where(t => t.Namespace == DomainNamespace || (t.Namespace?.StartsWith(DomainNamespace + ".", StringComparison.Ordinal) ?? false))
            .ToList();
        Assert.Contains(typeof(Shipment), domainTypes);

        var violations = domainTypes
            .SelectMany(t => ReferencedTypes(t).Select(r => (Owner: t, Referenced: r)))
            .Where(x => IsForbidden(x.Referenced))
            .Select(x => $"{x.Owner.FullName} -> {x.Referenced.FullName}")
            .Distinct()
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Shipments_module_does_not_reference_operations_module()
    {
        // ADR-015: Shipments olay yayınlar ve view okur; Operations'a derleme zamanı bağımlılığı yoktur.
        var references = typeof(Shipment).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("Lst.Modules.Operations", references);
        Assert.Contains("Lst.Modules.Operations", typeof(Lst.Modules.Operations.OperationsModule).Assembly.GetName().Name);
    }

    [Fact]
    public void Tracking_module_depends_on_shipments_contract_but_not_vice_versa()
    {
        // ADR-016: Tracking, carrier olayını IShipmentCarrierEvents üzerinden uygular; Shipments ve Operations Tracking'i bilmez.
        var tracking = typeof(Lst.Modules.Tracking.TrackingModule).Assembly;
        Assert.Contains("Lst.Modules.Shipments", tracking.GetReferencedAssemblies().Select(a => a.Name));
        Assert.DoesNotContain("Lst.Modules.Operations", tracking.GetReferencedAssemblies().Select(a => a.Name));
        Assert.DoesNotContain("Lst.Modules.Tracking", typeof(Shipment).Assembly.GetReferencedAssemblies().Select(a => a.Name));
        Assert.DoesNotContain("Lst.Modules.Tracking",
            typeof(Lst.Modules.Operations.OperationsModule).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    [Fact]
    public void Scanner_detects_a_known_dependency()
    {
        // Tarayıcının gerçekten çalıştığının kanıtı: Infrastructure tipi EF Core'a referans verir.
        var infrastructure = typeof(Shipment).Assembly.GetTypes()
            .Single(t => t.Name == "ShipmentsModelContributor");
        Assert.Contains(ReferencedTypes(infrastructure), IsForbidden);
    }

    private static bool IsForbidden(Type type)
    {
        var name = type.Namespace ?? string.Empty;
        return ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
            || (type.IsGenericType && type.GetGenericArguments().Any(IsForbidden));
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var i in type.GetInterfaces())
        {
            yield return i;
        }

        foreach (var a in type.GetCustomAttributesData())
        {
            yield return a.AttributeType;
        }

        foreach (var f in type.GetFields(all))
        {
            yield return f.FieldType;
        }

        foreach (var p in type.GetProperties(all))
        {
            yield return p.PropertyType;
        }

        foreach (var m in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (m is MethodInfo mi)
            {
                yield return mi.ReturnType;
            }

            foreach (var p in m.GetParameters())
            {
                yield return p.ParameterType;
            }

            foreach (var t in IlReferencedTypes(m))
            {
                yield return t;
            }
        }
    }

    private static List<Type> IlReferencedTypes(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return [];
        }

        var found = new List<Type>();
        var i = 0;
        while (i < il.Length)
        {
            OpCode op;
            if (il[i] == 0xFE)
            {
                op = OpCodeTable[unchecked((short)(0xFE00 | il[i + 1]))];
                i += 2;
            }
            else
            {
                op = OpCodeTable[il[i]];
                i += 1;
            }

            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = Resolve(method, BitConverter.ToInt32(il, i));
                switch (member)
                {
                    case Type t:
                        found.Add(t);
                        break;
                    case MethodInfo mi:
                        found.Add(mi.DeclaringType!);
                        found.Add(mi.ReturnType);
                        break;
                    case ConstructorInfo ci:
                        found.Add(ci.DeclaringType!);
                        break;
                    case FieldInfo fi:
                        found.Add(fi.DeclaringType!);
                        found.Add(fi.FieldType);
                        break;
                }
            }

            i += OperandSize(op, il, i);
        }

        return found;
    }

    private static MemberInfo? Resolve(MethodBase method, int token)
    {
        try
        {
            var typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            return method.Module.ResolveMember(token, typeArgs, methodArgs);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int position) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 4,
    };
}

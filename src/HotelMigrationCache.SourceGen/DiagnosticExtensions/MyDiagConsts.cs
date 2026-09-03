using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace HotelMigrationCache.SourceGen.DiagnosticExtensions;

public static class MyDiagConsts
{
    public static readonly DiagnosticDescriptor BS001 = new DiagnosticDescriptor("BS001",
                    "Class must be partial",
                    "Class '{0}' must be declared as partial to generate serialization code",
                    "BinarySerializer",
                    DiagnosticSeverity.Warning,
                    true);

    public static readonly DiagnosticDescriptor BS002 = new DiagnosticDescriptor(
                        "BS002",
                        "No serializable properties",
                        "Class '{0}' has no public properties with supported types",
                        "BinarySerializer",
                        DiagnosticSeverity.Warning,
                        true);
}

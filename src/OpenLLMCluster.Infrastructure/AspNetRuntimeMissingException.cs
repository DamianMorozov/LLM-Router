namespace OpenLLMCluster.Infrastructure;

/// <summary>Отсутствует совместимый .NET или ASP.NET Core Runtime, необходимый дочернему агенту</summary>
public sealed class AspNetRuntimeMissingException(string architecture, string diagnosticOutput)
    : InvalidOperationException("Для запуска агента требуются совместимые .NET и ASP.NET Core Runtime 10 (" + architecture + ")")
{
    public string Architecture { get; } = architecture;
    public string DiagnosticOutput { get; } = diagnosticOutput;

    /// <summary>Распознаёт отсутствие или несовместимую версию обоих runtime по сообщению apphost</summary>
    public static Exception FromAgentExit(int exitCode, string output)
    {
        if ((output.Contains("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
            && (exitCode == unchecked((int)0x80008096) || output.Contains("install or update .NET", StringComparison.OrdinalIgnoreCase)
                || output.Contains("No frameworks were found", StringComparison.OrdinalIgnoreCase)))
        {
            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            foreach (var candidate in new[] { "x64", "arm64", "x86" })
                if (output.Contains("Architecture: " + candidate, StringComparison.OrdinalIgnoreCase)) { architecture = candidate; break; }
            return new AspNetRuntimeMissingException(architecture, output);
        }
        return new InvalidOperationException("Агент завершился при запуске: " + output);
    }
}

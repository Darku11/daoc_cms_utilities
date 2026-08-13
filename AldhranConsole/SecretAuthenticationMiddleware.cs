/* SPDX-License-Identifier: GPL-3.0-only */
using System.Security.Cryptography;
using System.Text;

namespace AldhranConsole;

internal sealed class SecretAuthenticationMiddleware
{
    private const string HeaderName = "X-Aldhran-Secret";
    private readonly RequestDelegate _next;

    public SecretAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ConsoleOptions options)
    {
        // The liveness probe deliberately exposes no database, bridge or secret data.
        if (string.Equals(
            context.Request.Path.Value,
            "/health",
            StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        string supplied = context.Request.Headers[HeaderName].ToString();
        if (!FixedTimeEquals(supplied, options.ApiSecret))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "Unauthorized" });
            return;
        }

        await _next(context);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}

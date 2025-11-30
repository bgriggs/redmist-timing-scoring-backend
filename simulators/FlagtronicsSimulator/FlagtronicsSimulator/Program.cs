using System.Net;
using System.Net.Sockets;

namespace FlagtronicsSimulator;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure Kestrel to listen on port 52733
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, 52733);
        });

        // Add services to the container.
        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthorization();

        app.MapControllers();

        // Log API access information
        var apiKey = "12345678-1234-1234-1234-123456789abc";
        var localIp = GetLocalIPAddress();
        
        Console.WriteLine("DriverID API started on port 52733");
        Console.WriteLine("Access URLs:");
        Console.WriteLine($"  Local:    http://localhost:52733/api/driverid/{apiKey}");
        if (!string.IsNullOrEmpty(localIp))
        {
            Console.WriteLine($"  LAN:      http://{localIp}:52733/api/driverid/{apiKey}");
        }
        Console.WriteLine($"Active API key: {apiKey}");
        Console.WriteLine();
        Console.WriteLine("Test with: curl http://localhost:52733/api/driverid/{apiKey}/health");
        Console.WriteLine("Simulate new event: POST http://localhost:52733/api/driverid/{apiKey}/simulate");
        Console.WriteLine();

        app.Run();
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
        }
        catch
        {
            // Ignore errors getting local IP
        }
        return string.Empty;
    }
}

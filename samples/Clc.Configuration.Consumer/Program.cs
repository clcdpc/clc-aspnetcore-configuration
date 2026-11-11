using System;
using System.Threading.Tasks;
using Clc.Configuration;
using Microsoft.AspNetCore.Builder;

public static class Program
{
    private const string ApplicationDirectoryName = "clc-configuration-consumer";

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddClcConfiguration(ApplicationDirectoryName);

        await using var app = builder.Build();
        Console.WriteLine($"Loaded external configuration for {ApplicationDirectoryName}.");
    }
}

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using VilarDriverApi.Data;
using VilarDriverApi.Services;

var builder = WebApplication.CreateBuilder(args);

// =====================
// Global culture
// =====================
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// =====================
// Configuration check
// =====================
var connectionString = builder.Configuration.GetConnectionString("Default");

Console.WriteLine("=======================================");
Console.WriteLine("🚀 APPLICATION STARTING");
Console.WriteLine($"🌍 ENVIRONMENT: {builder.Environment.EnvironmentName}");
Console.WriteLine($"🔗 CONNECTION STRING PRESENT: {!string.IsNullOrWhiteSpace(connectionString)}");
Console.WriteLine("=======================================");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("❌ Connection string 'Default' is missing.");
}

// =====================
// DB
// =====================
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        connectionString,
        sql =>
        {
            // 🔁 IMPORTANT for Azure SQL (serverless / cold start)
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null
            );
        });
});

// =====================
// Services
// =====================
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EpodService>();

// =====================
// Controllers + Swagger
// =====================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "VilarDriverApi",
        Version = "v1"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Bearer {JWT}"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// =====================
// JWT
// =====================
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var jwt = builder.Configuration.GetSection("Jwt");
var keyBytes = Encoding.UTF8.GetBytes(jwt["Key"]!);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            RoleClaimType = "role",
            NameClaimType = "sub"
        };
    });

builder.Services.AddAuthorization();

// =====================
// Storage
// =====================
var storagePath = builder.Configuration["Storage:BasePath"] ?? "Storage";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, storagePath));

// =====================
// BUILD APP
// =====================
var app = builder.Build();

// =====================
// Middleware
// =====================
app.UseSwagger();
app.UseSwaggerUI();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(app.Environment.ContentRootPath, storagePath)),
    RequestPath = "/files"
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// =====================
// DB MIGRATIONS (CONTROLLED)
// =====================
var runMigrationsRaw = Environment.GetEnvironmentVariable("RUN_DB_MIGRATIONS");
var runMigrations = string.Equals(runMigrationsRaw, "true", StringComparison.OrdinalIgnoreCase);

Console.WriteLine("=======================================");
Console.WriteLine($"🧪 RUN_DB_MIGRATIONS (raw): {runMigrationsRaw}");
Console.WriteLine($"🧪 RUN_DB_MIGRATIONS (parsed): {runMigrations}");
Console.WriteLine("=======================================");

if (runMigrations)
{
    Console.WriteLine("⚙️ STARTING DATABASE MIGRATIONS");

    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine("📦 Applying EF Core migrations...");
        db.Database.Migrate();

        Console.WriteLine("🌱 Running database seeder...");
        DbSeeder.Seed(db);

        Console.WriteLine("✅ DATABASE MIGRATIONS COMPLETED");
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ DATABASE MIGRATIONS FAILED");
        Console.WriteLine(ex.ToString());

        // ❗ NIE ZABIJA aplikacji w produkcji
    }
}
else
{
    Console.WriteLine("ℹ️ RUN_DB_MIGRATIONS is FALSE – skipping migrations");
}

// =====================
// RUN
// =====================
Console.WriteLine("✅ APPLICATION STARTED");
app.Run();

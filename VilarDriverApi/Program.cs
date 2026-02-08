using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using VilarDriverApi.Data;
using VilarDriverApi.Services;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

// =====================
// Global culture
// =====================
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// =====================
// Basic startup logs
// =====================
Console.WriteLine("=======================================");
Console.WriteLine("🚀 APPLICATION STARTING");
Console.WriteLine($"🌍 ENVIRONMENT: {builder.Environment.EnvironmentName}");
Console.WriteLine("=======================================");

// =====================
// Connection string (safe)
// =====================
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("❌ Connection string 'Default' is MISSING/EMPTY. Check App Service setting: ConnectionStrings__Default");
}
else
{
    try
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        Console.WriteLine($"✅ SQL CONFIG | Server={csb.DataSource} | Database={csb.InitialCatalog} | UserID={csb.UserID} | Encrypt={csb.Encrypt}");
    }
    catch
    {
        Console.WriteLine("⚠️ SQL CONFIG | Could not parse connection string (but it is present).");
    }
}

// =====================
// DB (register even if CS missing, to keep app running)
// =====================
builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (builder.Environment.IsDevelopment())
    {
        var sqliteCs = builder.Configuration.GetConnectionString("Sqlite")
                      ?? "Data Source=app.dev.db";
        opt.UseSqlite(sqliteCs);
    }
    else
    {
        opt.UseSqlServer(
            builder.Configuration.GetConnectionString("Default"),
            sql => sql.EnableRetryOnFailure(
                maxRetryCount: 8,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null
            ));
    }
});

// =====================
// CORS (Frontend -> API)
// =====================
// IMPORTANT: Must be enabled for the exact SWA origin to allow OPTIONS preflight + JWT Authorization header.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var origins =
            builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? Array.Empty<string>();

        if (origins.Length == 0)
        {
            // Bez originów: nie otwieramy CORS na świat.
            // Zostawiamy politykę "zamkniętą" => przeglądarka i tak zablokuje cross-site.
            return;
        }

        policy
             .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD")
            .WithHeaders("Authorization", "Content-Type", "Accept", "Origin", "X-Requested-With")
            // jeśli w FE potrzebujesz odczytać nazwę pliku z nagłówka przy download:
            .WithExposedHeaders("Content-Disposition")
            // JWT w Authorization header => nie potrzebujemy cookies/credentials
            .DisallowCredentials();
    });
});

// =====================
// Services
// =====================
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EpodService>();
builder.Services.AddSingleton<BlobStorageService>();

// =====================
// Controllers + Swagger
// =====================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "VilarDriverApi", Version = "v1" });

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
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// =====================
// JWT (safe)
// =====================
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

if (string.IsNullOrWhiteSpace(jwtKey) || string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
{
    Console.WriteLine("⚠️ JWT config missing (Jwt:Key/Issuer/Audience). Auth may fail until you set App Service settings.");
}
else
{
    var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

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
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                RoleClaimType = "role",
                NameClaimType = "sub"
            };
        });

    builder.Services.AddAuthorization();
}

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
// Exception handler (gives response instead of blank crash pages)
// =====================
app.UseExceptionHandler(appError =>
{
    appError.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        var ex = feature?.Error;

        if (ex is SqlException sqlEx && sqlEx.Number == 40613)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Database is warming up. Try again in a moment.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("Internal Server Error");
    });
});

// =====================
// Swagger + static files
// =====================
app.UseSwagger();
app.UseSwaggerUI();

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(app.Environment.ContentRootPath, storagePath)),
    RequestPath = "/files"
});

// =====================
// CORS MUST be here (before auth) so OPTIONS preflight works
// =====================
app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// =====================
// Controlled migrations
// =====================
var runMigrationsRaw = Environment.GetEnvironmentVariable("RUN_DB_MIGRATIONS");
var runMigrations = string.Equals(runMigrationsRaw, "true", StringComparison.OrdinalIgnoreCase);

Console.WriteLine($"🧪 RUN_DB_MIGRATIONS raw='{runMigrationsRaw}' parsed={runMigrations}");

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
    }
}
else
{
    Console.WriteLine("ℹ️ RUN_DB_MIGRATIONS is FALSE – skipping migrations");
}

Console.WriteLine("✅ APPLICATION STARTED");
app.Run();

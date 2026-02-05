using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using VilarDriverApi.Data;
using VilarDriverApi.Services;
using System.Globalization;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// =====================
// DB
// =====================
var connectionString = builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("❌ Connection string 'Default' is NULL or EMPTY");
}
else
{
    var csb = new SqlConnectionStringBuilder(connectionString);
    Console.WriteLine(
        $"✅ SQL CONFIG | Server={csb.DataSource} | Database={csb.InitialCatalog} | UserID={csb.UserID} | Encrypt={csb.Encrypt}"
    );
}

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(connectionString));

// =====================
// Services
// =====================
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EpodService>();

// =====================
// Controllers
// =====================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// =====================
// CORS (LOCALHOST ONLY)
// =====================
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// =====================
// JWT
// =====================
var jwt = builder.Configuration.GetSection("Jwt");
var keyBytes = Encoding.UTF8.GetBytes(jwt["Key"]!);

// KLUCZOWE: wyłącz mapowanie claimów
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

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
// Swagger + JWT
// =====================
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
        Description = "Wpisz: Bearer {twój_token_JWT}"
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
// Storage folder
// =====================
var storage = builder.Configuration["Storage:BasePath"] ?? "Storage";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, storage));

// =====================
// BUILD
// =====================
var app = builder.Build();

// =====================
// Swagger
// =====================
app.UseSwagger();
app.UseSwaggerUI();

// =====================
// Static files /files/...
// =====================
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(app.Environment.ContentRootPath, storage)),
    RequestPath = "/files"
});

// =====================
// Middleware
// =====================
app.UseCors("DevCors");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// =====================
// DB MIGRATION + SEED
// =====================
if (app.Environment.IsProduction())
{
    Console.WriteLine("ℹ️ Production environment detected – skipping Database.Migrate() and DbSeeder");
}
else
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine("ℹ️ Running Database.Migrate()");
        db.Database.Migrate();

        Console.WriteLine("ℹ️ Running DbSeeder");
        DbSeeder.Seed(db);

        Console.WriteLine("✅ Database initialized successfully");
    }
    catch (Exception ex)
    {
        Console.WriteLine("❌ Database initialization FAILED");
        Console.WriteLine(ex.ToString());
        throw;
    }
}

app.Run();

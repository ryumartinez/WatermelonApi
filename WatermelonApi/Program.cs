using Microsoft.EntityFrameworkCore;
using WatermelonApi;
using Testcontainers.MsSql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 1. Initialize and Start the Container (SQL Server 2022)
var dbContainer = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
    .Build();

await dbContainer.StartAsync();
var connectionString = dbContainer.GetConnectionString();

// 2. Register Services
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Global snake_case policy for WatermelonDB compatibility
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.AddDbContext<AppDbContext>(opt => 
    opt.UseSqlServer(connectionString));

builder.Services.AddScoped<WatermelonService>();

var app = builder.Build();

// 3. Seed the Database on Startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Ensure migrations/schema are applied to the container
    await context.Database.EnsureCreatedAsync();
    await SeedData(context);
}

app.MapOpenApi();
app.MapScalarApiReference();
app.MapControllers();

app.Run();

// --- Updated Seeding Logic for Enterprise Product Schema ---
async Task SeedData(AppDbContext context)
{
    if (await context.Products.AnyAsync()) return;

    var random = new Random();
    
    // Seed Data Arrays
    var brands = new[] { ("NK", "Nike"), ("AD", "Adidas"), ("AP", "Apple"), ("SN", "Sony"), ("LG", "Logitech") };
    var colors = new[] { ("01", "Black"), ("02", "White"), ("03", "Red"), ("04", "Blue"), ("05", "Silver") };
    var sizes = new[] { ("S", "Small"), ("M", "Medium"), ("L", "Large"), ("XL", "Extra Large") };
    var adjectives = new[] { "Premium", "Pro", "Ultra", "Classic", "Limited", "Essential" };
    var categories = new[] { "Headset", "Sneakers", "Watch", "Controller", "Bottle" };
    var dataAreas = new[] { "US01", "PY01", "BR01" }; // D365 Companies

    Console.WriteLine("Seeding 100,000 enterprise-grade products...");
    
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var products = new List<Product>();

    for (int i = 1; i <= 100000; i++)
    {
        var brand = brands[random.Next(brands.Length)];
        var color = colors[random.Next(colors.Length)];
        var size = sizes[random.Next(sizes.Length)];
        var area = dataAreas[random.Next(dataAreas.Length)];
        
        var productName = $"{adjectives[random.Next(adjectives.Length)]} {brand.Item2} {categories[random.Next(categories.Length)]}";
        
        products.Add(new Product
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{productName} {i}",
            ItemId = $"ITEM-{random.Next(1000, 9999)}-{i:D4}",
            BarCode = $"789{random.NextInt64(1000000000, 9999999999)}",
            BrandCode = brand.Item1,
            BrandName = brand.Item2,
            ColorCode = color.Item1,
            ColorName = color.Item2,
            SizeCode = size.Item1,
            SizeName = size.Item2,
            Unit = "PCS",
            DataAreaId = area,
            InventDimId = $"DIM-{Guid.NewGuid().ToString()[..8].ToUpper()}",
            IsRequiredBatchId = random.Next(10) > 8, // 20% chance of requiring batch
            LastModified = now,
            ServerCreatedAt = now,
            IsDeleted = false
        });

        // Batch processing for performance
        if (i % 5000 == 0)
        {
            await context.Products.AddRangeAsync(products);
            await context.SaveChangesAsync();
            products.Clear();
            Console.WriteLine($"Progress: {i}/100,000 records inserted.");
        }
    }
    
    Console.WriteLine("Seeding complete.");
}
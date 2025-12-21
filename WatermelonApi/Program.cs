using Microsoft.EntityFrameworkCore;
using WatermelonApi;
using Testcontainers.MsSql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 1. Initialize and Start the Container
var dbContainer = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
    .Build();

await dbContainer.StartAsync();
var connectionString = dbContainer.GetConnectionString();

// 2. Register Services using the Container's Connection String
builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(opt => 
    opt.UseSqlServer(connectionString));

builder.Services.AddScoped<WatermelonService>();

var app = builder.Build();



// 3. Seed the Database on Startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
    await SeedData(context);
}

app.MapOpenApi();
app.MapScalarApiReference();
app.MapControllers();
app.Run();

// --- Updated Seeding Logic ---
async Task SeedData(AppDbContext context)
{
    if (await context.Products.AnyAsync()) return;

    var random = new Random();
    var adjectives = new[] { "Premium", "Organic", "Digital", "Ultra", "Classic", "Vintage", "Smart", "Eco", "Pro", "Turbo" };
    var categories = new[] { "Gadget", "Apparel", "Home", "Office", "Wellness", "Kitchen", "Outdoor", "Automotive", "Pet", "Travel" };
    var nouns = new[] { "Hub", "Pack", "System", "Gear", "Set", "Unit", "Kit", "Device", "Solution", "Item" };

    Console.WriteLine("Seeding 100,000 realistic products... please wait.");
    
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var products = new List<Product>();

    for (int i = 1; i <= 100000; i++)
    {
        // Generates names like "Organic Wellness Kit" or "Turbo Office Solution"
        var productName = $"{adjectives[random.Next(adjectives.Length)]} {categories[random.Next(categories.Length)]} {nouns[random.Next(nouns.Length)]}";
        
        products.Add(new Product
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{productName} {i}", // Appending 'i' ensures uniqueness for search testing
            Price = Math.Round((decimal)random.NextDouble() * 500 + 5, 2), // Random price between 5 and 505
            Sku = $"SKU-{random.Next(100, 999)}-{i:D5}",
            LastModified = now,
            ServerCreatedAt = now,
            IsDeleted = false
        });

        if (i % 10000 == 0)
        {
            await context.Products.AddRangeAsync(products);
            await context.SaveChangesAsync();
            products.Clear();
            Console.WriteLine($"Inserted {i} products...");
        }
    }
}
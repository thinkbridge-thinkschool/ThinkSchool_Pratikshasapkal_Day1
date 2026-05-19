using OrderApi.Interfaces;
using OrderApi.Repositories;
using OrderApi.Services;
using OrderApi.Strategies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddScoped<IDiscountStrategy, NoDiscountStrategy>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddScoped<IOrderRepository, OrderRepository>();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program
{
}
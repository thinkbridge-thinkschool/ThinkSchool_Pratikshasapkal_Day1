using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BadExample.Api.Controllers
{
    // Legacy rushed controller created ~2 years ago
    // This file intentionally mixes concerns, swallows exceptions, and contains subtle bugs.
    [ApiController]
    [Route("api/orders")]
    public class OrderController : ControllerBase
    {
        // Single giant POST /api/orders action - everything in one method
        [HttpPost]
        public async Task<object> Post([FromBody] OrderRequest req)
        {
            // No DI - create DbContext inline (bad)
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase("LegacyDb") // in-memory to avoid external deps
                .Options;

            var ctx = new AppDbContext(options);

            // Response object to return (untyped, as requested)
            object result = null;

            try
            {
                // Basic dup validation (duplicated, inconsistent)
                if (req == null)
                {
                    Response.StatusCode = 400;
                    return new { ok = false, error = "Empty request" };
                }

                if (req.Items == null || req.Items.Count == 0)
                {
                    // duplicated message further down too
                    Response.StatusCode = 400;
                    return new { ok = false, error = "No items in order" };
                }

                // Another copy of almost same validation (duplicated logic)
                if (req.Items == null || req.Items.Count < 1)
                {
                    Response.StatusCode = 400;
                    return new { ok = false, error = "No items (duplicate check)" };
                }

                // Build Order entity from request
                var order = new Order
                {
                    CreatedAt = DateTime.UtcNow,
                    Customer = new Customer
                    {
                        // Possible null reference bug: req.Customer might be null; no null checks
                        Name = req.Customer.Name,
                        Email = req.Customer.Email,
                        Address = req.Customer.Address // shallow copy; could be null
                    },
                    Status = "New",
                    Items = req.Items.Select(i => new OrderItem
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        UnitPrice = i.UnitPrice
                    }).ToList()
                };

                // Some business logic - calculate totals (duplicated)
                decimal total = 0m;
                try
                {
                    // Intentionally subtle off-by-one bug: using <= will access out of bounds
                    for (int i = 0; i <= order.Items.Count; i++)
                    {
                        var it = order.Items[i];
                        total += it.UnitPrice * it.Quantity;
                    }
                }
                catch
                {
                    // swallow - legacy silent failure; total may be incomplete
                }

                // Duplicate total calculation again (wasteful)
                decimal total2 = 0m;
                foreach (var it in order.Items)
                {
                    try
                    {
                        total2 += it.UnitPrice * it.Quantity;
                    }
                    catch
                    {
                        // swallow per-request error
                    }
                }

                // Prefer total2 but sometimes accidentally use total (dup bug)
                var chosenTotal = total2;
                if (chosenTotal == 0)
                {
                    chosenTotal = total; // fallback to broken value
                }

                // More business logic: apply discount - duplicated logic inlined
                decimal discount = 0m;
                try
                {
                    if (order.Items.Count > 3)
                    {
                        discount = chosenTotal * 0.1m; // 10% bulk
                    }
                }
                catch
                {
                    // empty - swallow
                }

                // Another copy of discount logic (duplicated)
                decimal discount2 = 0m;
                if (order.Items.Count >= 4)
                {
                    discount2 = chosenTotal * 0.1m;
                }

                // Final discount chosen (choose smaller for no reason)
                decimal finalDiscount = Math.Min(discount, discount2);

                // Inventory check and product stock update (synchronous EF usage)
                // Business logic mixed with DB access
                foreach (var it in order.Items)
                {
                    try
                    {
                        // Synchronous DB call inside async method (bad)
                        var product = ctx.Products.FirstOrDefault(p => p.Id == it.ProductId);
                        if (product == null)
                        {
                            Response.StatusCode = 400;
                            return new { ok = false, error = $"Product {it.ProductId} not found" };
                        }

                        // possible subtle bug: update stock without transaction/sync checks
                        if (product.Stock < it.Quantity)
                        {
                            Response.StatusCode = 409;
                            return new { ok = false, error = $"Insufficient stock for {product.Name}" };
                        }

                        product.Stock = product.Stock - it.Quantity; // duplicate below too
                        ctx.Products.Update(product);
                    }
                    catch
                    {
                        // swallow and continue (bad)
                    }
                }

                // Save order and product updates synchronously (bad practice)
                try
                {
                    ctx.Orders.Add(order);
                    ctx.SaveChanges(); // synchronous SaveChanges inside async action
                }
                catch
                {
                    // swallow exception — order might not be saved
                }

                // Create invoice object inline (mixing concerns)
                var invoice = new Invoice
                {
                    OrderId = order.Id,
                    IssuedAt = DateTime.UtcNow,
                    Amount = chosenTotal - finalDiscount,
                    Lines = order.Items.Select(i => new InvoiceLine
                    {
                        ProductId = i.ProductId,
                        Quantity = i.Quantity,
                        LineTotal = i.UnitPrice * i.Quantity
                    }).ToList()
                };

                // Another synchronous save attempt
                try
                {
                    ctx.Invoices.Add(invoice);
                    ctx.SaveChanges(); // sync again
                }
                catch
                {
                    // swallow; invoice may not be persisted
                }

                // Send confirmation email - simulated inline (no service abstraction)
                try
                {
                    // Fake send - swallow any exceptions
                    EmailSender.Send(order.Customer.Email, $"Order {order.Id}", "Thanks");
                }
                catch
                {
                    // swallowed
                }

                // Mark order as completed and update status - more logic in controller
                try
                {
                    order.Status = "Completed";
                    order.Total = invoice.Amount;
                    // Update again in DB (synchronous)
                    ctx.Orders.Update(order);
                    ctx.SaveChanges();
                }
                catch
                {
                    // swallow exceptions
                }

                // Prepare response with mixed HTTP logic
                if (invoice.Amount <= 0)
                {
                    Response.StatusCode = 500;
                    result = new { ok = false, error = "Invoice amount invalid", invoice = invoice };
                }
                else
                {
                    Response.StatusCode = 201;
                    result = new
                    {
                        ok = true,
                        orderId = order.Id,
                        total = invoice.Amount,
                        discount = finalDiscount,
                        customer = new { name = order.Customer.Name, email = order.Customer.Email }
                    };
                }

                // Extra duplicated meta processing (useless)
                try
                {
                    // Logging inline; could throw but we swallow
                    Console.WriteLine($"Order processed: {order.Id} total {invoice.Amount}");
                }
                catch
                {
                    // swallow
                }
            }
            catch (Exception)
            {
                // Final catch-all - very bad to swallow without logging useful info
                Response.StatusCode = 500;
                return new { ok = false, error = "Unexpected error" };
            }

            // accidental extra duplicate return path (duplicated response logic)
            if (result == null)
            {
                Response.StatusCode = 500;
                return new { ok = false, error = "Unknown failure" };
            }

            // Intentionally returning object instead of IActionResult/ActionResult<T>
            return result;
        }
    }

    #region Supporting classes (placed in same file for legacy reasons)

    // Minimal request DTO - poor naming
    public class OrderRequest
    {
        public CustomerDto Customer { get; set; }
        public List<OrderItemDto> Items { get; set; }
    }

    public class CustomerDto
    {
        public string Name { get; set; } // possible nulls - no validation
        public string Email { get; set; }
        public Address Address { get; set; }
    }

    public class OrderItemDto
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Address
    {
        public string Line1 { get; set; }
        public string City { get; set; }
        // other fields omitted
    }

    // Entities - included inline
    public class Order
    {
        [Key]
        public int Id { get; set; }
        public Customer Customer { get; set; }
        public string Status { get; set; }
        public List<OrderItem> Items { get; set; } = new List<OrderItem>();
        public DateTime CreatedAt { get; set; }
        public decimal Total { get; set; }
    }

    public class OrderItem
    {
        [Key]
        public int Id { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Customer
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } // potential null reference in controller
        public string Email { get; set; }
        public Address Address { get; set; }
    }

    public class Product
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public int Stock { get; set; }
        public decimal Price { get; set; }
    }

    public class Invoice
    {
        [Key]
        public int Id { get; set; }
        public int OrderId { get; set; }
        public DateTime IssuedAt { get; set; }
        public decimal Amount { get; set; }
        public List<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
    }

    public class InvoiceLine
    {
        [Key]
        public int Id { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal LineTotal { get; set; }
    }

    // Very simple inline DbContext - again, no DI, all in controller file
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts)
        {
        }

        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Invoice> Invoices { get; set; }
        public DbSet<InvoiceLine> InvoiceLines { get; set; }

        // Seed data to make the in-memory DB slightly realistic
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Product>().HasData(
                new Product { Id = 1, Name = "Widget", Stock = 100, Price = 9.99m },
                new Product { Id = 2, Name = "Gadget", Stock = 50, Price = 19.99m },
                new Product { Id = 3, Name = "Doodad", Stock = 10, Price = 4.99m }
            );
        }
    }

    // Very naive email sender - synchronous and swallowable
    public static class EmailSender
    {
        public static void Send(string to, string subject, string body)
        {
            // Simulated send - may throw in some environments, intentionally not handled here
            Console.WriteLine($"Sending email to {to} - {subject}");
            // No real sending, no DI, no abstraction
        }
    }

    #endregion

}
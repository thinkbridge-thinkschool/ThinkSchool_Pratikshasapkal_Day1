using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Dtos;

public class CreateQuoteRequest
{
    [Required]
    public string Author { get; set; } = string.Empty;

    [Required]
    public string Text { get; set; } = string.Empty;
}
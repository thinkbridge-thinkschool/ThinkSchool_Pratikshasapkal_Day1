namespace QuotesApi.Models;

public class User
{
    public int Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    private User()
    {
    }

    public User(
        string email,
        string password)
    {
        Email = email;

        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
    }

    public bool VerifyPassword(string password)
    {
        return BCrypt.Net.BCrypt.Verify(
            password,
            PasswordHash);
    }
}
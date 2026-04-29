using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

// 1. Rejestracja kontrolerów
builder.Services.AddControllers();

// 2. Dodanie Swaggera (Zamiast AddOpenApi, który jest uboższy)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 3. Rejestracja połączenia do bazy danych (ConnectionString)
// Używamy danych z Twojego zadania i bazy ClinicAdoNet
var connectionString = "Server=localhost,1433;Database=ClinicAdoNet;User Id=SA;Password=yourStrong(!)Password;TrustServerCertificate=True";

var app = builder.Build();

// 4. Włączenie interfejsu Swaggera w przeglądarce
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(); // To sprawi, że pod /swagger zobaczysz UI
}

app.UseHttpsRedirection();
app.UseAuthorization();

// 5. PRZYKŁADOWY ENDPOINT - Test połączenia z bazą w Dockerze
app.MapGet("/test-db", () =>
{
    using var connection = new SqlConnection(connectionString);
    try
    {
        connection.Open();
        // Próba pobrania nazwy pierwszej specjalizacji z tabeli[cite: 1]
        var command = new SqlCommand("SELECT TOP 1 Name FROM Specializations", connection);
        var result = command.ExecuteScalar();
        return Results.Ok(new { Message = "Połączono z Dockerem!", Data = result });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Błąd: {ex.Message}");
    }
});

app.MapControllers();

app.Run();
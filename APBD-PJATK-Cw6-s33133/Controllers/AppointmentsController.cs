using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using WebApplication1.DTO; // Pamiętaj o podmianie przestrzeni nazw

namespace WebApplication1.Controllers
{
    [ApiController] // Mówi ASP.NET, że to kontroler API (automatycznie waliduje żądania)
    [Route("api/[controller]")] // Adres to będzie /api/appointments
    public class AppointmentsController : ControllerBase
    {
        private readonly string _connectionString;

        // Wstrzykujemy IConfiguration, aby wyciągnąć ConnectionString z appsettings.json
        public AppointmentsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? throw new InvalidOperationException("Brak connection stringa.");
        }

        // 1. GET /api/appointments
        [HttpGet]
        public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
        {
            var appointments = new List<AppointmentListDto>();

            // Używamy "await using", co gwarantuje, że po zakończeniu bloku kodu 
            // połączenie zostanie bezpiecznie zamknięte, nawet jeśli wystąpi błąd.
            await using var connection = new SqlConnection(_connectionString);
            
            // Definiujemy komendę SQL.
            await using var command = new SqlCommand("""
                SELECT 
                    a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, 
                    p.FirstName + N' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                WHERE (@Status IS NULL OR a.Status = @Status)
                  AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
                ORDER BY a.AppointmentDate;
                """, connection);

            // Dodajemy parametry zamiast "sklejać" stringi. To zapobiega atakom SQL Injection!
            command.Parameters.AddWithValue("@Status", string.IsNullOrEmpty(status) ? DBNull.Value : status);
            command.Parameters.AddWithValue("@PatientLastName", string.IsNullOrEmpty(patientLastName) ? DBNull.Value : patientLastName);

            await connection.OpenAsync(); // Otwieramy połączenie z bazą
            await using var reader = await command.ExecuteReaderAsync(); // Wykonujemy zapytanie (Read)

            // Dopóki są wiersze do przeczytania z bazy, czytamy je i mapujemy na nasz DTO
            while (await reader.ReadAsync())
            {
                appointments.Add(new AppointmentListDto
                {
                    IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                    AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                    Status = reader.GetString(reader.GetOrdinal("Status")),
                    Reason = reader.GetString(reader.GetOrdinal("Reason")),
                    PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                    PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
                });
            }

            return Ok(appointments); // Zwracamy status 200 OK z listą
        }

        // 2. GET /api/appointments/{id}
        [HttpGet("{idAppointment}")]
        public async Task<IActionResult> GetAppointment(int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand("""
                SELECT 
                    a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                    p.Email AS PatientEmail, p.PhoneNumber AS PatientPhoneNumber,
                    d.LicenseNumber AS DoctorLicenseNumber, d.FirstName + N' ' + d.LastName AS DoctorFullName
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
                JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
                WHERE a.IdAppointment = @IdAppointment;
                """, connection);

            command.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await connection.OpenAsync();
            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." }); // Status 404
            }

            var dto = new AppointmentDetailsDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes")) ? string.Empty : reader.GetString(reader.GetOrdinal("InternalNotes")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
                PatientPhoneNumber = reader.GetString(reader.GetOrdinal("PatientPhoneNumber")),
                DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber")),
                DoctorFullName = reader.GetString(reader.GetOrdinal("DoctorFullName"))
            };

            return Ok(dto);
        }

        // 3. POST /api/appointments
        [HttpPost]
        public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto dto)
        {
            // Walidacje biznesowe
            if (dto.AppointmentDate < DateTime.Now)
                return BadRequest(new ErrorResponseDto { Message = "Termin wizyty nie może być w przeszłości." });

            if (string.IsNullOrWhiteSpace(dto.Reason) || dto.Reason.Length > 250)
                return BadRequest(new ErrorResponseDto { Message = "Powód wizyty jest wymagany i nie może przekraczać 250 znaków." });

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Sprawdzamy czy lekarz ma już wizytę (ExecuteScalar zwraca pojedynczą wartość, tu: COUNT)
            await using var checkDoctorConflictCmd = new SqlCommand(
                "SELECT COUNT(1) FROM Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate", connection);
            checkDoctorConflictCmd.Parameters.AddWithValue("@IdDoctor", dto.IdDoctor);
            checkDoctorConflictCmd.Parameters.AddWithValue("@AppointmentDate", dto.AppointmentDate);
            var count = (int)(await checkDoctorConflictCmd.ExecuteScalarAsync() ?? 0);
            if (count > 0)
                return Conflict(new ErrorResponseDto { Message = "Lekarz ma już zaplanowaną wizytę w tym terminie." }); // Status 409

            // Wstawiamy wizytę do bazy i od razu pobieramy jej nowo wygenerowane ID (OUTPUT INSERTED.IdAppointment)
            await using var insertCmd = new SqlCommand("""
                INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
                OUTPUT INSERTED.IdAppointment
                VALUES (@IdPatient, @IdDoctor, @AppointmentDate, 'Scheduled', @Reason);
                """, connection);

            insertCmd.Parameters.AddWithValue("@IdPatient", dto.IdPatient);
            insertCmd.Parameters.AddWithValue("@IdDoctor", dto.IdDoctor);
            insertCmd.Parameters.AddWithValue("@AppointmentDate", dto.AppointmentDate);
            insertCmd.Parameters.AddWithValue("@Reason", dto.Reason);

            try
            {
                var newId = (int)(await insertCmd.ExecuteScalarAsync() ?? 0);
                return Created($"/api/appointments/{newId}", null); // Status 201
            }
            catch (SqlException)
            {
                // Łapiemy błąd z bazy, najczęściej z powodu braku pacjenta/lekarza (klucz obcy)
                return BadRequest(new ErrorResponseDto { Message = "Niepoprawne dane. Sprawdź czy pacjent i lekarz istnieją." });
            }
        }

        // 4. PUT /api/appointments/{id}
        [HttpPut("{idAppointment}")]
        public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto dto)
        {
            var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
            if (!allowedStatuses.Contains(dto.Status))
                return BadRequest(new ErrorResponseDto { Message = "Nieprawidłowy status wizyty." });

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Sprawdzamy obecny stan wizyty w bazie
            await using var getAppCmd = new SqlCommand("SELECT Status, AppointmentDate FROM Appointments WHERE IdAppointment = @IdAppointment", connection);
            getAppCmd.Parameters.AddWithValue("@IdAppointment", idAppointment);
            await using var reader = await getAppCmd.ExecuteReaderAsync();
            
            if (!await reader.ReadAsync())
                return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });

            var currentStatus = reader.GetString(reader.GetOrdinal("Status"));
            var currentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate"));
            await reader.CloseAsync(); // Musimy zamknąć Readera, by móc wykonać kolejne zapytanie na tym samym połączeniu

            if (currentStatus == "Completed" && currentDate != dto.AppointmentDate)
                return BadRequest(new ErrorResponseDto { Message = "Nie można zmienić terminu wizyty, która jest już zakończona." });

            // Jeśli zmieniono datę, sprawdzamy konflikt z harmonogramem lekarza
            if (currentDate != dto.AppointmentDate)
            {
                await using var checkDoctorConflictCmd = new SqlCommand(
                    "SELECT COUNT(1) FROM Appointments WHERE IdDoctor = @IdDoctor AND AppointmentDate = @AppointmentDate AND IdAppointment != @IdAppointment", connection);
                checkDoctorConflictCmd.Parameters.AddWithValue("@IdDoctor", dto.IdDoctor);
                checkDoctorConflictCmd.Parameters.AddWithValue("@AppointmentDate", dto.AppointmentDate);
                checkDoctorConflictCmd.Parameters.AddWithValue("@IdAppointment", idAppointment);
                
                var count = (int)(await checkDoctorConflictCmd.ExecuteScalarAsync() ?? 0);
                if (count > 0)
                    return Conflict(new ErrorResponseDto { Message = "Lekarz ma już wizytę w nowo wybranym terminie." });
            }

            // Wykonujemy właściwy Update (ExecuteNonQueryAsync bo nie oczekujemy zwracanych danych)
            await using var updateCmd = new SqlCommand("""
                UPDATE Appointments 
                SET IdPatient = @IdPatient, IdDoctor = @IdDoctor, AppointmentDate = @AppointmentDate, 
                    Status = @Status, Reason = @Reason, InternalNotes = @InternalNotes
                WHERE IdAppointment = @IdAppointment
                """, connection);

            updateCmd.Parameters.AddWithValue("@IdPatient", dto.IdPatient);
            updateCmd.Parameters.AddWithValue("@IdDoctor", dto.IdDoctor);
            updateCmd.Parameters.AddWithValue("@AppointmentDate", dto.AppointmentDate);
            updateCmd.Parameters.AddWithValue("@Status", dto.Status);
            updateCmd.Parameters.AddWithValue("@Reason", dto.Reason);
            updateCmd.Parameters.AddWithValue("@InternalNotes", string.IsNullOrEmpty(dto.InternalNotes) ? DBNull.Value : dto.InternalNotes);
            updateCmd.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await updateCmd.ExecuteNonQueryAsync();

            return Ok(); // Status 200
        }

        // 5. DELETE /api/appointments/{id}
        [HttpDelete("{idAppointment}")]
        public async Task<IActionResult> DeleteAppointment(int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var getStatusCmd = new SqlCommand("SELECT Status FROM Appointments WHERE IdAppointment = @IdAppointment", connection);
            getStatusCmd.Parameters.AddWithValue("@IdAppointment", idAppointment);
            
            var status = (string?)(await getStatusCmd.ExecuteScalarAsync());

            if (status == null)
                return NotFound(new ErrorResponseDto { Message = "Wizyta nie istnieje." });

            if (status == "Completed")
                return Conflict(new ErrorResponseDto { Message = "Nie można usunąć wizyty, która została już zakończona." });

            await using var deleteCmd = new SqlCommand("DELETE FROM Appointments WHERE IdAppointment = @IdAppointment", connection);
            deleteCmd.Parameters.AddWithValue("@IdAppointment", idAppointment);
            await deleteCmd.ExecuteNonQueryAsync();

            return NoContent(); // Status 204
        }
    }
}
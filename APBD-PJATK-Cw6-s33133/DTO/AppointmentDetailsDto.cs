namespace WebApplication1.DTO;

public class AppointmentDetailsDto
{
    public int IdAppointment { get; set; }
    public DateTime AppointmentDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string InternalNotes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string PatientEmail { get; set; } = string.Empty;
    public string PatientPhoneNumber { get; set; } = string.Empty;
    public string DoctorLicenseNumber { get; set; } = string.Empty;
    public string DoctorFullName { get; set; } = string.Empty;
}
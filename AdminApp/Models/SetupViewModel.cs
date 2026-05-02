using System.ComponentModel.DataAnnotations;

namespace AdminApp.Models;

public class SetupViewModel
{
    [Required(ErrorMessage = "Name darf nicht leer sein")]
    [Display(Name = "Admin Username")]
    public string Username { get; set; }

    [Required(ErrorMessage = "Passwort darf nicht leer sein")]
    [DataType(DataType.Password)]
    [Display(Name = "Passwort")]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,}$", ErrorMessage = "Das Passwort muss mindestens 8 Zeichen lang sein und Groß-, Kleinbuchstaben, Zahl und Sonderzeichen enthalten.")]
    public string Password { get; set; }

    [Required(ErrorMessage = "Bitte Passwort bestätigen")]
    [DataType(DataType.Password)]
    [Display(Name = "Passwort bestätigen")]
    [Compare("Password", ErrorMessage = "Die Passwörter stimmen nicht überein.")]
    public string ConfirmPassword { get; set; }
}

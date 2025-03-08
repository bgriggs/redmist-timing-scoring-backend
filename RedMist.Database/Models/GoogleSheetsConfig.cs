using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class GoogleSheetsConfig
{
    [Key]
    public int Id { get; set; }

    [MaxLength(4000)]
    public string Json { get; set; } = string.Empty;
}

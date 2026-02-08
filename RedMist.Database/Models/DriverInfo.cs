using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

public class DriverInfo
{
    public int Id { get; set; }
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;
    public long? FlagtronicsId { get; set; }
}

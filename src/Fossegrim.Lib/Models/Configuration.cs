using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Fossegrim.Lib.Models;

public class Configuration
{
    public List<MediaFolder>? Folders { get; set; }
}



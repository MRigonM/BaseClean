using Microsoft.AspNetCore.Identity;

namespace BaseClean.Domain.Entities;

public class AppUser : IdentityUser
{
    public string Name { get; set; } 
}
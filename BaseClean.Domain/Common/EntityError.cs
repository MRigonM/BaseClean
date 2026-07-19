namespace BaseClean.Domain.Common;

public class EntityError
{
    public static Error NotFound(int id) => new Error($"NotFound", $"ID {id} was not found.");
    
    public static Error Forbidden => new Error("Auth.Forbidden", "You do not have access to this resource.");
    
    public static Error NoChangesDetected => new Error($"NoChanges", "No changes were detected during the operation.");

    public static Error CreationFailed => new Error($"CreationFailed", $"Creation failed. No changes were made to the database.");

    public static Error CreationUnexpectedError => new Error($"CreationUnexpectedError", $"An unexpected error occurred during creation.");

    public static Error RetrievalError => new Error($"RetrievalError", $"An error occurred while retrieving the list of entity.");

    public static Error UpdateUnexpectedError => new Error($"UpdateUnexpectedError", "An unexpected error occurred during the update operation.");

    public static Error DeletionUnexpectedError => new Error($"DeletionUnexpectedError", "An unexpected error occurred during the deletion operation.");
}
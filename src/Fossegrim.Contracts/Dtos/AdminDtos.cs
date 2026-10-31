namespace Fossegrim.Contracts.Dtos;

public record MediaFolderDto(Guid Id, string Name, string Location, DateTime? DateAdded, DateTime? DateLastScanned);

public record AddMediaFolderRequest(string Name, string Location);

public record ApiErrorResponse(string Error);

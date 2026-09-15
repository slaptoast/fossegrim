namespace Fossegrim.Web.Shared;

public record ContextMenuAction(string Label, Func<Task> OnSelect);

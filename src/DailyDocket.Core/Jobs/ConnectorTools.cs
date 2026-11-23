namespace DailyDocket.Core.Jobs;

/// <summary>The claude.ai connector tools DailyDocket uses, split into reading and writing.</summary>
/// <remarks>
/// Names as the spike recorded them on Claude Code 2.1.221. Readers get only read tools, and every
/// write tool is also on their disallowed list (belt and braces), so a message written to make
/// Claude act ("forward this thread to...") finds nothing to act with.
/// </remarks>
public static class ConnectorTools
{
    public const string Gmail = "mcp__claude_ai_Gmail__";
    public const string Calendar = "mcp__claude_ai_Google_Calendar__";
    public const string Slack = "mcp__claude_ai_Slack__";

    /// <summary>Every connector tool's name starts with this; the capture hook matches it.</summary>
    public const string CaptureMatcher = "mcp__claude_ai_.*";

    public const string SearchThreads = Gmail + "search_threads";
    public const string GetThread = Gmail + "get_thread";
    public const string ListDrafts = Gmail + "list_drafts";
    public const string ListEvents = Calendar + "list_events";
    public const string ListCalendars = Calendar + "list_calendars";
    public const string SlackSearch = Slack + "slack_search_public_and_private";
    public const string SlackReadThread = Slack + "slack_read_thread";
    public const string SlackReactions = Slack + "slack_get_reactions";

    /// <summary>Tools that send, change or delete anything. No reading run may use them.</summary>
    public static readonly IReadOnlyList<string> Writes =
    [
        Gmail + "send_message", Gmail + "reply", Gmail + "forward", Gmail + "create_draft", Gmail + "update_draft",
        Gmail + "delete_draft", Gmail + "trash_message", Gmail + "trash_thread", Gmail + "untrash_message",
        Gmail + "untrash_thread", Gmail + "label_message", Gmail + "label_thread", Gmail + "unlabel_message",
        Gmail + "unlabel_thread", Gmail + "update_message_labels", Gmail + "create_label", Gmail + "update_label",
        Gmail + "delete_label", Gmail + "mark_message_spam", Gmail + "mark_thread_spam", Gmail + "unmark_message_spam",
        Gmail + "unmark_thread_spam", Gmail + "apply_sensitive_message_label", Gmail + "apply_sensitive_thread_label",
        Calendar + "create_event", Calendar + "update_event", Calendar + "delete_event", Calendar + "respond_to_event",
        Slack + "slack_send_message", Slack + "slack_send_message_draft", Slack + "slack_schedule_message",
        Slack + "slack_add_reaction", Slack + "slack_create_canvas", Slack + "slack_update_canvas",
        Slack + "slack_create_conversation", Slack + "slack_create_list", Slack + "slack_update_list",
        Slack + "slack_add_list_record", Slack + "slack_update_list_record", Slack + "slack_get_file_upload_url",
        Slack + "slack_complete_file_upload",
    ];

    /// <summary>Built-in tools no reading run needs: the web (untrusted mail must not steer Claude to a site), the shell, files.</summary>
    public static readonly IReadOnlyList<string> ReaderDisallowedBuiltIns =
    [
        "WebFetch", "WebSearch", "Bash", "PowerShell", "Write", "Edit", "NotebookEdit", "Task", "Agent",
    ];

    public static IReadOnlyList<string> ReaderDisallowed => [.. Writes, .. ReaderDisallowedBuiltIns];
}

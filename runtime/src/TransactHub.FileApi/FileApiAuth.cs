namespace TransactHub.FileApi;

/// <summary>
/// The File API's roles (R-09-76), per tenant, from the bank's role map (R-06-28). Its callers are system clients of the bank's
/// identity provider (client credentials), but for <see cref="Uploader"/>, a person at the bank's own upload screen.
/// <list type="table">
/// <item><term>file-sender</term><description>a channel or a corporate system: <c>POST /files</c></description></item>
/// <item><term>file-gateway</term><description>the SFTP gateway handing in a file it picked up: <c>POST /files</c> with channel <c>sftp</c></description></item>
/// <item><term>file-uploader</term><description>a person uploading for a corporate: <c>POST /files/ui</c></description></item>
/// <item><term>file-reader</term><description>a payment's files and their bytes: <c>GET /files</c></description></item>
/// <item><term>file-outbox</term><description>a host whose workflows send files: <c>POST /outbox</c></description></item>
/// <item><term>file-resender</term><description>the operations and service consoles' hosts: <c>POST /files/{id}/resend</c></description></item>
/// </list>
/// </summary>
public static class FileApiAuth
{
    public const string Sender = "file-sender", Gateway = "file-gateway", Uploader = "file-uploader", Reader = "file-reader", Outbox = "file-outbox", Resender = "file-resender";
    public static readonly string[] Roles = [Sender, Gateway, Uploader, Reader, Outbox, Resender];
}

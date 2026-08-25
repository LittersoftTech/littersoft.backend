namespace Pawfront.Application.Blocks;

/// <summary>
/// Who the current request is, for the purposes of blocking.
/// </summary>
/// <remarks>
/// <para>
/// This exists because provider discovery and the five booking searches have to
/// hide a blocked provider, and their method signatures carry no caller: the
/// filter shape is shared by six call sites and threading a viewer through all of
/// them would change every one. A scoped accessor keeps the change to the one
/// decorator that needs it -- the same reasoning that put the daily agenda's
/// caller behind <c>ICurrentPetParentContext</c>.
/// </para>
/// <para>
/// Each host registers its own, BEFORE calling <c>AddPawfrontApplication()</c>,
/// which TryAdds <see cref="NullCurrentBlockParty"/> as the fallback. A host with
/// no discovery surface (the chat host) simply never overrides it, and filters
/// nothing.
/// </para>
/// </remarks>
public interface ICurrentBlockParty
{
    /// <summary>
    /// The caller, or null when there is nobody to resolve -- an unauthenticated
    /// request, a host that does not model one, or a pet parent who has not
    /// finished their profile. Null filters NOTHING, which is the right default:
    /// somebody with no identity has placed no blocks and had none placed on them.
    /// </summary>
    Task<BlockParty?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The fallback: nobody. Registered by the Application layer so every host
/// resolves, and left in place by hosts that have no discovery surface to filter.
/// </summary>
internal sealed class NullCurrentBlockParty : ICurrentBlockParty
{
    public Task<BlockParty?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BlockParty?>(null);
}

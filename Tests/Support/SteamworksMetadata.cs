// TEST HOST ONLY. Scenario contains this opaque Workshop ID, but the harness stubs Find.Scenario
// and never calls Steam APIs. Use the real DLL when available; otherwise opt in with
// STEAMWORKS_METADATA_STUB=1. This file is outside Source and is never shipped in Parametric.dll.
namespace Steamworks
{
    public struct PublishedFileId_t
    {
        public ulong m_PublishedFileId;
    }
}

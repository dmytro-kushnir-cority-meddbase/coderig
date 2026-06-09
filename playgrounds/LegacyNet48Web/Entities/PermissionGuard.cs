using System;

namespace LegacyNet48Web.Entities
{
    // Fixture for throw-fact extraction + throw-matched effects: the "permission gate behind a read"
    // shape. AssertRight throws when the right is absent; ReadGuarded routes through it — mirroring
    // *Cache.New -> IfCanView -> CertificateEntity.AssertRight -> throw in the real codebase. None of
    // these match a method/ctor effect rule, so only a MatchThrow rule can surface the guard.
    public sealed class AccessDeniedException : Exception
    {
    }

    public static class PermissionGuard
    {
        public static void AssertRight(int recordId)
        {
            if (recordId == 0)
                throw new AccessDeniedException();
        }

        public static int ReadGuarded(int recordId)
        {
            AssertRight(recordId);
            return recordId;
        }
    }
}

Implement atomic batched transfers in the existing ledger project. Validate the
entire batch before changing balances, preserve transfer order, reject duplicate
transfer IDs, and make replaying the same accepted batch idempotent. Keep the
existing balance query behavior. The project file is an explicitly editable
path because the new source file must be compiled.

import pytest
from conftest import implementation

from corel_agent.contracts import AgentError


def test_private_directory_has_current_user_protected_acl(tmp_path):
    import win32api
    import win32con
    import win32security
    module = implementation('storage_acl')
    directory = tmp_path / 'owned'
    module.ensure_private(directory)
    descriptor = win32security.GetNamedSecurityInfo(str(directory), win32security.SE_FILE_OBJECT,
        win32security.DACL_SECURITY_INFORMATION | win32security.OWNER_SECURITY_INFORMATION)
    assert descriptor.GetSecurityDescriptorControl()[0] & win32security.SE_DACL_PROTECTED
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32con.TOKEN_QUERY)
    try:
        sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
        acl = descriptor.GetSecurityDescriptorDacl()
        assert acl.GetAceCount() >= 1
        assert all(acl.GetAce(index)[2] == sid for index in range(acl.GetAceCount()))
    finally:
        token.Close()


def test_existing_permissive_directory_is_not_silently_claimed(tmp_path):
    directory = tmp_path / 'existing'
    directory.mkdir()
    (directory / 'history.sqlite3').write_bytes(b'sentinel')
    with pytest.raises(AgentError) as caught:
        implementation('storage_acl').ensure_private(directory)
    assert caught.value.code == 'STORAGE_ACL_UNSAFE'
    assert (directory / 'history.sqlite3').read_bytes() == b'sentinel'


def test_private_root_rejects_permissive_existing_descendant(tmp_path):
    import win32con
    import win32security
    module = implementation('storage_acl')
    root = tmp_path / 'private'
    module.ensure_private(root)
    child = root / 'history.sqlite3'
    child.write_bytes(b'preserve history')
    acl = win32security.ACL()
    acl.AddAccessAllowedAce(win32security.ACL_REVISION, win32con.GENERIC_ALL,
                           win32security.CreateWellKnownSid(win32security.WinWorldSid, None))
    win32security.SetNamedSecurityInfo(str(child), win32security.SE_FILE_OBJECT,
        win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION,
        None, None, acl, None)
    try:
        with pytest.raises(AgentError) as caught:
            module.ensure_private(root)
        assert caught.value.code == 'STORAGE_ACL_UNSAFE'
    finally:
        child.unlink()


def test_inherited_private_children_are_accepted(tmp_path):
    module = implementation('storage_acl')
    root = tmp_path / 'private'
    module.ensure_private(root)
    (root / 'assets').mkdir()
    (root / 'assets' / 'fixture.txt').write_text('fixture')
    module.ensure_private(root)

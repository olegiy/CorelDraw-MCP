"""Create a private current-user data directory; never claim existing permissive data."""

from .assets import assert_no_links
from .contracts import AgentError


def ensure_private(directory):
    import win32api
    import win32con
    import win32file
    import win32security

    from .launcher import user_security

    assert_no_links(directory)
    if not directory.exists():
        win32file.CreateDirectory(str(directory), user_security())
        # Remove inheritance explicitly; descendants inherit only the current user's ACE.
        token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32con.TOKEN_QUERY)
        try:
            sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
            acl = win32security.ACL()
            acl.AddAccessAllowedAceEx(win32security.ACL_REVISION,
                win32con.OBJECT_INHERIT_ACE | win32con.CONTAINER_INHERIT_ACE,
                win32con.GENERIC_ALL, sid)
            win32security.SetNamedSecurityInfo(str(directory), win32security.SE_FILE_OBJECT,
                win32security.DACL_SECURITY_INFORMATION | win32security.PROTECTED_DACL_SECURITY_INFORMATION |
                win32security.OWNER_SECURITY_INFORMATION,
                sid, None, acl, None)
        finally:
            token.Close()
    assert_no_links(directory)
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32con.TOKEN_QUERY)
    try:
        sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
        descriptor = win32security.GetNamedSecurityInfo(str(directory), win32security.SE_FILE_OBJECT,
            win32security.DACL_SECURITY_INFORMATION | win32security.OWNER_SECURITY_INFORMATION)
        acl = descriptor.GetSecurityDescriptorDacl()
        valid = (descriptor.GetSecurityDescriptorOwner() == sid and
                 descriptor.GetSecurityDescriptorControl()[0] & win32security.SE_DACL_PROTECTED and
                 acl is not None and acl.GetAceCount() >= 1 and
                 all(acl.GetAce(index)[0][0] == 0 and acl.GetAce(index)[2] == sid
                     for index in range(acl.GetAceCount())))
        inheritance = win32con.OBJECT_INHERIT_ACE | win32con.CONTAINER_INHERIT_ACE
        valid = valid and any(acl.GetAce(index)[0][1] & inheritance == inheritance
                              for index in range(acl.GetAceCount()))
        if not valid:
            raise AgentError('STORAGE_ACL_UNSAFE', 'Каталог данных не имеет проверенного приватного ACL текущего пользователя; запуск запрещён.', 503)
        import os
        checked = 0
        for root, directories, files in os.walk(directory, followlinks=False):
            from pathlib import Path
            for name in [*directories, *files]:
                checked += 1
                if checked > 100000:
                    raise AgentError('STORAGE_ACL_UNSAFE', 'Превышен бюджет проверки ACL; запуск запрещён.', 503)
                child = Path(root) / name
                assert_no_links(child)
                descriptor = win32security.GetNamedSecurityInfo(str(child), win32security.SE_FILE_OBJECT,
                    win32security.DACL_SECURITY_INFORMATION | win32security.OWNER_SECURITY_INFORMATION)
                child_acl = descriptor.GetSecurityDescriptorDacl()
                if (descriptor.GetSecurityDescriptorOwner() != sid or child_acl is None or
                        child_acl.GetAceCount() == 0 or any(
                            child_acl.GetAce(index)[0][0] != 0 or child_acl.GetAce(index)[2] != sid
                            for index in range(child_acl.GetAceCount()))):
                    raise AgentError('STORAGE_ACL_UNSAFE', 'Вложенные данные имеют непроверенные права; содержимое не изменено.', 503)
    finally:
        token.Close()

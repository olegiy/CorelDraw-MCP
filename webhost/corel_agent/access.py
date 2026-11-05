"""Ephemeral operator sessions. No credential/bootstrap value is written to disk."""

import hashlib
import re
import secrets
import time
from threading import RLock

from .contracts import AgentError, SessionResponse

COOKIE_NAME = "corel_agent_session"
MAX_BODY_BYTES = 2 * 1024**2
CSP = (
    "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
    "img-src 'self' data: blob:; font-src 'self'; frame-ancestors 'none'; "
    "base-uri 'none'; object-src 'none'; form-action 'self'"
)
SECURITY_HEADERS = (
    (b"cache-control", b"no-store"), (b"x-content-type-options", b"nosniff"),
    (b"referrer-policy", b"no-referrer"), (b"content-security-policy", CSP.encode("ascii")),
)


class Access:
    def __init__(self, host="127.0.0.1:8878"):
        match = re.fullmatch(r"127\.0\.0\.1:([0-9]{1,5})", host)
        if not match or not 1 <= int(match[1]) <= 65535:
            raise AgentError("HOST_INVALID", "Host должен быть явным IPv4 loopback с портом.", 503)
        self.host, self.origin = host, f"http://{host}"
        self._bootstrap, self._sessions = {}, {}
        self._lock = RLock()

    @staticmethod
    def _digest(token):
        return hashlib.sha256(token.encode("utf-8")).hexdigest()

    def issue_bootstrap(self):
        token = secrets.token_urlsafe(32)
        now = time.monotonic()
        with self._lock:
            self._bootstrap = {key: expires for key, expires in self._bootstrap.items() if expires > now}
            while len(self._bootstrap) >= 32:
                self._bootstrap.pop(next(iter(self._bootstrap)))
            self._bootstrap[self._digest(token)] = now + 60
        return token

    def exchange(self, token):
        if not isinstance(token, str) or not re.fullmatch(r"[A-Za-z0-9_-]{43}", token):
            raise AgentError("BOOTSTRAP_INVALID", "Локальный вход истёк или недействителен.", 401)
        with self._lock:
            expires = self._bootstrap.pop(self._digest(token), None)
            if expires is None or expires <= time.monotonic():
                raise AgentError("BOOTSTRAP_INVALID", "Локальный вход истёк или уже использован.", 401)
            session, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
            while len(self._sessions) >= 32:
                self._sessions.pop(next(iter(self._sessions)))
            self._sessions[self._digest(session)] = csrf
            return session, SessionResponse(csrf_token=csrf)

    def session_response(self, request):
        key = getattr(request.state, "session_key", None)
        with self._lock:
            csrf = self._sessions.get(key)
        if csrf is None:
            raise AgentError("SESSION_REQUIRED", "Нужна действующая локальная сессия.", 401)
        return SessionResponse(csrf_token=csrf)

    async def check(self, request):
        if request.headers.getlist("host") != [self.host]:
            raise AgentError("HOST_DENIED", "Host не соответствует локальному launcher.", 403)
        origins = request.headers.getlist("origin")
        if origins and origins != [self.origin]:
            raise AgentError("ORIGIN_DENIED", "Запрос другого Origin запрещён.", 403)
        sites = request.headers.getlist("sec-fetch-site")
        if sites and (len(sites) != 1 or sites[0] not in {"same-origin", "none"}):
            raise AgentError("FETCH_METADATA_DENIED", "Cross-site доступ запрещён.", 403)
        unsafe = request.method not in {"GET", "HEAD", "OPTIONS"}
        if unsafe and origins != [self.origin]:
            raise AgentError("ORIGIN_REQUIRED", "Изменяющему запросу нужен точный Origin.", 403)
        if unsafe and request.headers.get("sec-fetch-mode") == "no-cors":
            raise AgentError("FETCH_METADATA_DENIED", "no-cors изменение запрещено.", 403)
        if not request.url.path.startswith("/api/"):
            return
        if request.url.path == "/api/v1/access/bootstrap" and request.method == "POST":
            if request.query_params or len(request.headers.getlist("x-corel-bootstrap")) != 1:
                raise AgentError("BOOTSTRAP_INVALID", "Bootstrap передаётся только одним защищённым заголовком.", 403)
            return
        cookies = request.headers.getlist("cookie")
        if len(cookies) > 1 or any(len(value) > 4096 for value in cookies):
            raise AgentError("SESSION_REQUIRED", "Нужна однозначная локальная cookie.", 401)
        token = request.cookies.get(COOKIE_NAME)
        if not token or not re.fullmatch(r"[A-Za-z0-9_-]{43}", token):
            raise AgentError("SESSION_REQUIRED", "Откройте панель через локальный launcher.", 401)
        key = self._digest(token)
        with self._lock:
            csrf = self._sessions.get(key)
        if csrf is None:
            raise AgentError("SESSION_REQUIRED", "Локальная сессия устарела; откройте панель через launcher.", 401)
        if unsafe:
            supplied = request.headers.getlist("x-corel-csrf")
            if (len(supplied) != 1 or not re.fullmatch(r"[A-Za-z0-9_-]{43}", supplied[0])
                    or not secrets.compare_digest(supplied[0], csrf)):
                raise AgentError("CSRF_DENIED", "CSRF token не совпадает с локальной сессией.", 403)
        if (request.url.path.startswith(("/api/v1/images/", "/api/v1/artifacts/"))
                and request.query_params):
            raise AgentError("PATH_QUERY_DENIED", "Артефакты не принимают query-пути.", 403)
        request.state.session_key = key

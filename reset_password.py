#!/usr/bin/env python3
"""Сброс пароля на сервере — на случай, если забыли пароль owner.
Запуск на server1: python3 reset_password.py owner НОВЫЙ_ПАРОЛЬ
Затем: systemctl --user restart epolisi-assist
"""
import hashlib
import json
import os
import secrets
import sys

PBKDF2_ITERATIONS = 200_000
CFG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")


def hash_pw(password):
    salt = secrets.token_hex(16)
    dk = hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), PBKDF2_ITERATIONS)
    return f"pbkdf2${PBKDF2_ITERATIONS}${salt}${dk.hex()}"


if len(sys.argv) != 3:
    print("Использование: python3 reset_password.py <логин> <новый_пароль>")
    raise SystemExit(1)

user, pw = sys.argv[1], sys.argv[2]
if len(pw) < 6:
    print("Пароль должен быть не короче 6 символов")
    raise SystemExit(1)

with open(CFG_PATH, encoding="utf-8") as f:
    cfg = json.load(f)
cfg.setdefault("users", {})[user] = hash_pw(pw)
with open(CFG_PATH, "w", encoding="utf-8") as f:
    json.dump(cfg, f, ensure_ascii=False, indent=2)
os.chmod(CFG_PATH, 0o600)
print(f"Пароль для «{user}» обновлён. Перезапустите: systemctl --user restart epolisi-assist")

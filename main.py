#!/usr/bin/env python3
import subprocess
import sys
import os
import time

def main():
    print("🐍 Python wrapper starting C# bot...")
    
    # Проверяем наличие .NET
    if not check_dotnet():
        print("❌ .NET not found, attempting to install...")
        install_dotnet()
    
    # Запускаем C# бота
    run_csharp_bot()

def check_dotnet():
    try:
        result = subprocess.run(['dotnet', '--version'], 
                              capture_output=True, text=True)
        if result.returncode == 0:
            print(f"✅ .NET SDK detected: {result.stdout.strip()}")
            return True
    except FileNotFoundError:
        pass
    return False

def install_dotnet():
    """Установка .NET SDK на хостинге"""
    commands = [
        'wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh',
        'chmod +x dotnet-install.sh',
        './dotnet-install.sh --channel 8.0',
        'export PATH="$HOME/.dotnet:$PATH"'
    ]
    
    for cmd in commands:
        subprocess.run(cmd, shell=True)
    
    # Обновляем PATH для текущей сессии
    os.environ['PATH'] = os.path.expanduser('~/.dotnet') + ':' + os.environ['PATH']

def run_csharp_bot():
    """Запуск вашего C# бота"""
    print("🚀 Launching C# bot...")
    
    # Убеждаемся, что .NET в PATH
    dotnet_path = os.path.expanduser('~/.dotnet')
    if dotnet_path not in os.environ['PATH']:
        os.environ['PATH'] = dotnet_path + ':' + os.environ['PATH']
    
    # Запускаем ваш C# бот
    try:
        process = subprocess.run(['dotnet', 'CardsTelegramBotPirozki.dll'], 
                               check=True)
    except subprocess.CalledProcessError as e:
        print(f"❌ C# bot error: {e}")
        sys.exit(1)
    except FileNotFoundError:
        print("❌ CardsTelegramBotPirozki.dll not found!")
        sys.exit(1)

if __name__ == "__main__":
    main()

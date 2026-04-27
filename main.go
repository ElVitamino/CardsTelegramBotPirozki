package main

import (
    "fmt"
    "log"
    "os"
    "os/exec"
    "path/filepath"
)

func main() {
    fmt.Println("🚀 Go wrapper starting C# bot...")
    
    // Проверяем наличие .NET
    if err := checkDotNet(); err != nil {
        log.Fatal("❌ .NET not found: ", err)
    }
    
    // Запускаем C# бота
    cmd := exec.Command("dotnet", "CardsTelegramBotPirozki.dll")
    cmd.Stdout = os.Stdout
    cmd.Stderr = os.Stderr
    cmd.Dir = getCurrentDir()
    
    if err := cmd.Run(); err != nil {
        log.Fatal("❌ C# bot failed: ", err)
    }
}

func checkDotNet() error {
    cmd := exec.Command("dotnet", "--version")
    if err := cmd.Run(); err != nil {
        return fmt.Errorf("dotnet not installed: %v", err)
    }
    fmt.Println("✅ .NET SDK detected")
    return nil
}

func getCurrentDir() string {
    dir, err := os.Getwd()
    if err != nil {
        return "."
    }
    return dir
}

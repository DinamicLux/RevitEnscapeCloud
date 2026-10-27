# ☁ RevitEnscapeCloud RT

**Visualizador Fotorrealista en Tiempo Real & Gestor de Nubes de Puntos Densas (.LAS / .RCS) para Autodesk Revit 2026 y Standalone.**

[![Platform](https://img.shields.io/badge/Platform-Autodesk%20Revit%202026-0696D7.svg)](https://www.autodesk.com/products/revit/overview)
[![Framework](https://img.shields.io/badge/.NET-8.0%20x64-512BD4.svg)](https://dotnet.microsoft.com/)
[![Graphics](https://img.shields.io/badge/Graphics-Three.js%20%7C%20WebGL2%20%7C%20EDL-10B981.svg)](https://threejs.org/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## 🚀 Características Principales

### 1. ☁ Soporte Nativo de Nubes de Puntos de Alta Densidad (.LAS y .RCS)
- **Carga Directa por Arrastrar y Soltar (Drop-in):** Soporta archivos `.LAS` (LASF binario estándar) y Autodesk ReCap `.RCS` (ADOCT Octree) de hasta cientos de millones de puntos.
- **Muestreo Uniforme Full-File:** Elimina el problema de grupos pequeños o "parches" en escaneos densos (p. ej. escaneos láser Riegl VZ-600i a 2 mm).
- **Alineación Inteligente de Cota y Terreno:**
  - `ground_match`: Casa la rasante más baja del terreno escaneado con la cota base del modelo de Revit.
  - Ajuste fino interactivo con slider $\Delta Z$ (milisegundos en vivo).
  - Coordenadas compartidas de proyecto.
  - Visualización de coordenadas 3D en tiempo real bajo el cursor con tooltip.

### 2. 🎨 Filtros de Apariencia Avanzados
- **True Color ReCap (con Brillo & Contraste):** Calibración visual idéntica a Autodesk ReCap Pro con slider de ganancia lumínica.
- **True Color Mejorado (Relleno de Puntos Blancos por Cercanía 3D):** Indexación espacial por vóxeles (Spatial Hash Grid) que detecta puntos quemados o sobreexpuestos ($RGB \approx [1,1,1]$) y los rellena con el color del punto más cercano con color verdadero.
- **Filtro de Altura desde el Punto Más Bajo de cada Sector:** Gradiente hipsométrico (Turbo / Viridis) relativo a la cota mínima del terreno en cada sector de 2m.
- **Segmentación de Entorno (Árboles vs. Suelo):** Diferenciación visual automática mediante Excess Green Index (ExG) y altura local.
- **Intensidad / Reflectancia Láser VZ-600i.**
- **Sombreado Volumétrico Eye-Dome Lighting (EDL):** Oclusión ambiental en espacio de pantalla para resaltar bordes y profundidad en nubes densas.
- **Tamaño de Splat ReCap (Sólido):** Permite desde `0.0 px` (subpíxel/oculto) hasta `25.0 px` con ajuste fino de 0.1 px y entrada numérica directa.

### 3. 🎯 Renderizado de Zona en Máxima Calidad (100% Densidad)
- **Radio Concéntrico al Observador:** Centrado estrictamente en la posición donde está parado el usuario ($X, Z$) y la rasante del suelo ($Y$).
- **Cobertura Cilíndrica Sin Huecos:** Elimina los recortes esféricos diagonales para que muros, techos, árboles completos y columnas permanezcan al 100% íntegros.
- **Bajo Demanda (No en Vivo):** Navegación fluida a 60 FPS sin tirones ni recargas automáticas. Solo renderiza la zona cuando pulsas `🎯 Renderizar Zona`.
- **1 Sola Zona a la Vez:** Destrucción y liberación automática de GPU de zonas anteriores.

### 4. 🚶‍♂️ Modos de Navegación & Recorrido de Cámara
- **Vuelo Libre / Órbita:** Desplazamiento ágil con teclado (WASD / Flechas) con velocidad ajustable en tiempo real (Shift + Rueda).
- **Modo Caminar con Detección de Suelo:** El avatar colisiona con el terreno de la nube de puntos y nunca cae en vacíos o huecos de escaneo.
- **Ruta de Cámara y Exportación MP4:** Grabación de escenas lineales o curvas con previsualización del tiempo estimado de renderizado según FPS y resolución antes de exportar.

---

## 📦 Estructura del Proyecto

```
RevitEnscapeCloud/
├── RevitEnscapeCloud.exe         # Aplicación Launcher & Gestor de Instalación
├── RevitEnscapeCloud.addin       # Manifiesto oficial para Autodesk Revit 2026
├── RevitEnscapeCloud.csproj      # Proyecto .NET
├── build.ps1                     # Script de compilación desatendida en PowerShell
├── src/
│   ├── Launcher/                 # Código fuente de RevitEnscapeCloud.exe
│   │   └── Program.cs
│   ├── WebViewer/                # Visualizador Three.js WebGL2
│   │   └── index.html
│   ├── App.cs                    # Punto de entrada IExternalApplication (Ribbon Tab)
│   ├── RendererWindow.cs         # Ventana WPF con Microsoft WebView2 integrado
│   ├── LiveSyncExternalHandler.cs# Sincronización en tiempo real con Revit API
│   ├── LasFileReader.cs          # Lector binario nativo de archivos .LAS
│   ├── PointCloudReader.cs       # Lector de nubes nativas de Revit
│   ├── SceneExportContext.cs     # Exportador de geometría Revit a Three.js
│   └── SceneModels.cs            # Modelos de datos y mallas
└── dist/                         # Compilación distribuible
    ├── RevitEnscapeCloud.dll
    ├── RevitEnscapeCloud.exe
    └── WebViewer/
        └── index.html
```

---

## 🛠️ Instalación y Uso

### Opción A: Con el Ejecutable `RevitEnscapeCloud.exe`
1. Haz doble clic en `RevitEnscapeCloud.exe`.
2. Para usarlo sin Revit: pulsa **`🌐 Iniciar Visualizador 3D Standalone`** y arrastra cualquier archivo `.LAS` o `.RCS`.
3. Para usarlo en Revit: pulsa **`⚡ Instalar / Actualizar Add-in en Revit 2026`** (se instalará de inmediato en `%APPDATA%\Autodesk\Revit\Addins\2026`).

### Opción B: Compilación desde PowerShell
Ejecuta en una terminal PowerShell:
```powershell
.\build.ps1
```

---

## 👤 Autor
Desarrollado por **DinamicLux** ([@DinamicLux](https://github.com/DinamicLux)).
Licencia MIT.

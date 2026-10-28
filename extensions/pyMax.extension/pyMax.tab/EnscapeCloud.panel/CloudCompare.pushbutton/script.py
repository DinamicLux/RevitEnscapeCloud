# -*- coding: utf-8 -*-
"""Nube Tipo CloudCompare
Abre el visor en modo de sombreado volumétrico qEDL (Eye-Dome Lighting) y splats adaptativos continuos.
"""
__title__ = "Tipo\nCloudCompare"
__author__ = "DinamicLux"

import clr
import os
from pyrevit import revit, UI, forms

candidates = [
    r"C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud\dist\RevitEnscapeCloud.dll",
    r"C:\Users\durre\AppData\Roaming\Autodesk\Revit\Addins\2026\RevitEnscapeCloud.dll"
]
base_scratch = r"C:\Users\durre\.gemini\antigravity\scratch\RevitEnscapeCloud"
if os.path.exists(base_scratch):
    for f in os.listdir(base_scratch):
        if f.startswith("dist_") and os.path.isdir(os.path.join(base_scratch, f)):
            candidates.insert(0, os.path.join(base_scratch, f, "RevitEnscapeCloud.dll"))

dll_path = None
for c in candidates:
    if os.path.exists(c):
        dll_path = c
        break

if dll_path:
    try:
        clr.AddReferenceToFileAndPath(dll_path)
        import RevitEnscapeCloud
        
        ui_app = revit.uiapp
        if RevitEnscapeCloud.App.SyncHandler is None:
            RevitEnscapeCloud.App.SyncHandler = RevitEnscapeCloud.LiveSyncExternalHandler()
            RevitEnscapeCloud.App.SyncExternalEvent = UI.ExternalEvent.Create(RevitEnscapeCloud.App.SyncHandler)
            
        win = RevitEnscapeCloud.RendererWindow.ShowOrActivate(
            ui_app, 
            RevitEnscapeCloud.App.SyncHandler, 
            RevitEnscapeCloud.App.SyncExternalEvent
        )
    except Exception as ex:
        forms.alert("Error al iniciar Modo CloudCompare: {}".format(ex), title="pyMax - EnscapeCloud")
else:
    forms.alert("No se encontró RevitEnscapeCloud.dll.", title="pyMax - EnscapeCloud")

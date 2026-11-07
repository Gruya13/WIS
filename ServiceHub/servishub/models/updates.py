"""
servishub.models.updates
========================
Pydantic modeli i šeme za status lokalnog skladišta Windows zakrpi (Updates Cache).
"""

from typing import List, Optional

try:
    from pydantic import BaseModel, Field
    HAS_PYDANTIC = True
except ImportError:
    HAS_PYDANTIC = False

    class BaseModel:
        """Fallback osnovna klasa za okruženja bez pydantic paketa."""
        def __init__(self, **kwargs):
            for k, v in kwargs.items():
                setattr(self, k, v)

        def model_dump(self):
            res = {}
            for k, v in self.__dict__.items():
                if isinstance(v, list):
                    res[k] = [
                        item.model_dump() if hasattr(item, "model_dump") else item
                        for item in v
                    ]
                elif hasattr(v, "model_dump"):
                    res[k] = v.model_dump()
                else:
                    res[k] = v
            return res

        def to_dict(self):
            return self.model_dump()

    def Field(default=None, **kwargs):
        return default


class UpdatePackageItem(BaseModel):
    """Metapodaci pojedinačnog paketa ažuriranja (.msu ili .cab)."""
    filename: str = Field(..., description="Naziv datoteke paketa (npr. Windows11.0-KB5129195-x64.msu)")
    kb: Optional[str] = Field(None, description="Identifikator Microsoft baze znanja (npr. KB5129195)")
    file_type: str = Field(..., description="Tip arhive ('msu' ili 'cab')")
    size_bytes: int = Field(..., description="Tačna veličina u bajtovima")
    size_mb: float = Field(..., description="Veličina paketa formatirana u megabajtima (MB)")
    modified_at: str = Field(..., description="Vreme poslednje izmene u ISO formatu")
    modified_timestamp: float = Field(..., description="Unix timestamp izmene datoteke")
    path: Optional[str] = Field(None, description="Puna ili relativna putanja do datoteke na serveru")

    def to_dict(self):
        return self.model_dump() if hasattr(self, "model_dump") else self.__dict__


# Alijas za klijentske module koji koriste UpdateFileItem
UpdateFileItem = UpdatePackageItem


class UpdatesStatusResponse(BaseModel):
    """Odgovor API servisa o statusu NAS/lokalnog skladišta Windows 11 zakrpi."""
    enabled: bool = Field(True, description="Da li je keš zakrpi konfigurisan i omogućen")
    available: bool = Field(..., description="Da li je definisani direktorijum fizički dostupan na disku")
    directory: str = Field(..., description="Konfigurisana putanja do skladišta zakrpi")
    total_packages: int = Field(0, description="Ukupan broj pronađenih validnih .msu i .cab paketa")
    total_size_bytes: int = Field(0, description="Ukupna veličina svih paketa u bajtovima")
    total_size_mb: float = Field(0.0, description="Ukupna veličina svih paketa u MB")
    kb_list: List[str] = Field(default_factory=list, description="Lista svih prepoznatih KB identifikatora")
    packages: List[UpdatePackageItem] = Field(default_factory=list, description="Detaljna lista pronađenih paketa")

    def to_dict(self):
        return self.model_dump() if hasattr(self, "model_dump") else self.__dict__

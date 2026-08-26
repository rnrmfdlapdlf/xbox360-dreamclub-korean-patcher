# Xbox 360 실기 호환성 조사 기록

작성일: 2026-08-26

## 조사 대상

- 드림클럽 패치 ISO
  - `D:\Codex\DREAM CLUB\Dream C Club (Japan)_repacked_20260826_180419.iso`
- 실기에서 동작한다고 확인된 아이마스 비교 ISO
  - `D:\Codex\DREAM CLUB\ImasKoreanPatcher\The Idolmaster [4E4D07DB]_translation2_common_baseline_test.iso`
- 비교 대상 패처
  - `DreamClubKoreanPatcher`
  - `ImasKoreanPatcher`

## 현재 결론

두 결과물은 모두 `extract-xiso`로 만든 데이터용 XISO이며, 내부 `default.xex`도 `Retail / Uncompressed / Not-Encrypted / XGD2 Only` 상태이다.

따라서 드림클럽 ISO의 실기 부팅 실패를 다음 두 항목만으로 설명할 수는 없다.

- 전체 원본 XGD2 디스크 구조가 아닌 점
- `default.xex`가 암호화되지 않은 점

실기에서 동작하는 아이마스 ISO도 위 조건이 같기 때문이다. 동일한 실기 환경과 실행 방식을 사용했다는 전제에서 현재 가장 유력한 차이는 드림클럽 패처가 `default.xex`에 새 번역 데이터 영역을 추가하면서 XEX 블록 구조를 수동으로 변경한다는 점이다.

이 원인은 아직 실기 A/B 테스트로 확정되지 않았다.

## ISO 구조 비교

| 항목 | 아이마스 ISO | 드림클럽 ISO |
|---|---:|---:|
| 파일 크기 | 5,002,690,560바이트 | 4,832,821,248바이트 |
| 첫 XDVDFS 서명 위치 | `0x00010000` | `0x00010000` |
| 두 번째 XDVDFS 서명 위치 | `0x000107EC` | `0x000107EC` |
| XDVDFS 루트 섹터 | 264 | 264 |
| 생성 도구 | extract-xiso 2.7.0 | extract-xiso 2.7.0 |

두 패처가 사용하는 `exiso.exe`의 SHA-256도 같다.

```text
293C152CE3C06368E7C16E75EE43A00551570910A90BEFCC633F06CDDB2E72DC
```

아이마스 패처의 ISO 생성 코드는 [MainForm.cs](../ImasKoreanPatcher/MainForm.cs#L1020), 드림클럽 패처의 ISO 생성 코드는 [PatchPipeline.cs](PatchPipeline.cs#L252)에 있다. 두 코드 모두 `exiso -c`를 사용한다.

공식 `extract-xiso` 설명에서도 `-c`는 디렉터리에서 XISO를 생성하는 기능으로 정의되어 있다.

- https://github.com/XboxDev/extract-xiso/blob/master/README.md

## default.xex 공통 상태

두 ISO에서 직접 추출한 `default.xex`를 XexTool 6.3으로 확인했다.

| 항목 | 아이마스 | 드림클럽 |
|---|---|---|
| 실행 형식 | Retail | Retail |
| 압축 표시 | Uncompressed | Uncompressed |
| 암호화 표시 | Not-Encrypted | Not-Encrypted |
| 모듈 | Title Module | Title Module |
| 미디어 | XGD2 Only | XGD2 Only |

아이마스 패처는 XEX 변환에 `-e d -c u`, 드림클럽 패처는 `-e u -c u`를 사용하지만, 현재 사용 중인 XexTool 6.3에서는 두 방식 모두 `Retail / Not-Encrypted / Uncompressed` 결과가 나왔다. 이 옵션 표기 차이는 현재 증상의 직접 원인으로 볼 수 없다.

## 핵심 차이: XEX 패치 방법

### 아이마스 패처

아이마스 패처는 변환된 XEX 전체를 고정 크기 `byte[]`로 읽고 기존 영역만 수정한 뒤 같은 배열을 다시 저장한다.

- XEX 읽기: [XexTextPatcher.cs](../ImasKoreanPatcher/XexTextPatcher.cs#L193)
- 같은 배열 저장: [XexTextPatcher.cs](../ImasKoreanPatcher/XexTextPatcher.cs#L228)
- 긴 문자열 처리도 기존 코드 케이브 안에서 수행: [XexTextPatcher.cs](../ImasKoreanPatcher/XexTextPatcher.cs#L500)

따라서 패치 전후에 XEX 파일 크기와 기본 압축 블록 배치를 확장하지 않는다.

아이마스 비교 ISO의 XEX 기본 블록 상태는 다음과 같다.

| 항목 | 값 |
|---|---:|
| XEX 파일 크기 | 5,189,632바이트 |
| 헤더 크기 | `0x2000` |
| 블록 개수 | 4 |
| 데이터 합계 | `0x4F0000` |
| 0 영역 합계 | `0x3D0000` |
| 가상 이미지 합계 | `0x8C0000` |
| 선언된 Image Size | `0x8C0000` |
| 마지막 블록 데이터 크기 | `0x10000` |

### 드림클럽 패처

드림클럽 패처는 번역문을 담을 새 `.kotext` 데이터를 만들고 이를 변환된 XEX 끝에 직접 추가한다.

- 관련 구현: [PatchPipeline.cs](PatchPipeline.cs#L625)
- 재배치된 번역 항목: 309개
- 수정된 포인터: 321개
- 추가 데이터: `0x2600`바이트(9,728바이트)

확인된 파일 크기는 다음과 같다.

| 단계 | 파일 크기 |
|---|---:|
| xextool 변환 직후 | 9,547,776바이트 (`0x91B000`) |
| 현재 패치 후 | 9,557,504바이트 (`0x91D600`) |
| 증가량 | 9,728바이트 (`0x2600`) |

패처는 마지막 XEX 기본 블록의 데이터 크기를 `0x90000`에서 `0x92600`으로 직접 변경한다.

| 항목 | 변환 직후 | 현재 패치 후 |
|---|---:|---:|
| 데이터 합계 | `0x918000` | `0x91A600` |
| 0 영역 합계 | `0x120000` | `0x120000` |
| 가상 이미지 합계 | `0xA38000` | `0xA3A600` |
| 선언된 Image Size | `0xA40000` | `0xA40000` |
| 마지막 블록 데이터 크기 | `0x90000` | `0x92600` |

또한 패치 결과를 XexTool로 다시 PE로 추출했을 때 PE 섹션 테이블에는 `.kotext` 섹션이 존재하지 않았다. 원래 마지막 섹션은 `.reloc`이며, 새 번역 데이터는 PE 섹션 항목이 아니라 수정된 XEX 기본 블록을 통해 이미지 뒤쪽에 적재되는 구조이다.

## XexTool 재처리 결과

현재 드림클럽 패치 XEX를 XexTool 6.3에서 다시 `Not-Encrypted / Uncompressed` 형식으로 출력하면 파일과 블록 구조가 바뀐다.

| 항목 | 현재 패치 XEX | XexTool 재처리 후 |
|---|---:|---:|
| 파일 크기 | 9,557,504바이트 | 9,580,544바이트 |
| 파일 크기 차이 | - | `0x5A00`바이트 증가 |
| 데이터 합계 | `0x91A600` | `0x920000` |
| 가상 이미지 합계 | `0xA3A600` | `0xA40000` |
| 마지막 블록 데이터 크기 | `0x92600` | `0x98000` |

이는 현재 수동 생성 결과와 XexTool이 다시 직렬화한 결과가 동일하지 않다는 뜻이다. 현재 드림클럽 XEX가 Xenia에서는 실행되지만 실기에서 첫 부팅에 실패한다는 현상과 관련될 가능성이 가장 높다.

## Xenia에서 실행되는 이유에 대한 관찰

Xenia는 자체 XEX 로더에서 기본 압축 블록의 `data_size`와 `zero_size`를 읽어 이미지를 구성하고, XEX 페이지 설명자에 따라 전체 메모리를 확보한 다음 0으로 초기화한다.

- https://github.com/xenia-project/xenia/blob/master/src/xenia/cpu/xex_module.cc

따라서 Xenia에서 실행된다는 사실은 번역 데이터와 포인터가 에뮬레이터의 XEX 로더에서는 읽힌다는 것을 보여주지만, 실제 Xbox 360 로더에서도 같은 방식으로 허용된다는 것을 보장하지는 않는다.

## 현재 배제된 차이

다음 항목은 아이마스와 드림클럽 결과물이 같으므로 두 게임의 실기 구동 차이를 직접 설명하지 못한다.

- XISO의 XDVDFS 시작 위치
- 사용한 `exiso.exe` 버전과 실행 파일 해시
- `exiso -c` 재패킹 방식
- XEX의 Retail 표시
- XEX의 Not-Encrypted 표시
- XEX의 Uncompressed 표시
- XGD2 Only 미디어 표시

## 아직 확인되지 않은 사항

- 실제 테스트 Xbox 360이 RGH, JTAG, DVD 펌웨어 개조 중 어떤 환경인지
- 두 ISO를 정확히 같은 로더와 같은 저장 장치에서 실행했는지
- 드림클럽 XEX를 XexTool로 재처리한 뒤 만든 ISO가 실기에서 부팅되는지
- 새 번역 영역을 사용하지 않는 기존 크기의 드림클럽 XEX가 같은 실기에서 부팅되는지
- 실기에서 표시되는 구체적인 오류 메시지 또는 대시보드 복귀 상태

## 원인 확인을 위한 최소 시험안

현재 가설을 확인하려면 리소스와 ISO 구성은 그대로 유지하고 `default.xex`만 다르게 한 두 결과물을 비교해야 한다.

1. 현재 방식으로 만든 XEX를 사용하는 ISO
2. 현재 패치 XEX를 XexTool로 한 번 재처리한 뒤 사용하는 ISO

두 번째 결과물만 실기에서 부팅되면 수동 XEX 확장 또는 블록 직렬화가 원인으로 확정된다. 두 결과물이 모두 실패한다면 다음 단계로 새 번역 영역을 사용하지 않는 고정 크기 XEX와 비교해야 한다.

## 조사 중 변경 사항

- 소스 코드는 변경하지 않았다.
- 원본 ISO와 결과 ISO를 변경하지 않았다.
- 조사 과정에서 생성한 비교용 XEX와 PE 사본은 모두 삭제했다.

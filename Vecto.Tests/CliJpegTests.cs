using System.Security.Cryptography;
using Vecto.Cli;
using Vecto.Core;
using Xunit;

namespace Vecto.Tests;

/// <summary>
/// The CLI must decode JPEGs exactly like the Windows app (WPF/WIC = libjpeg-turbo with default settings).
/// Fixtures: small synthetic JPEGs written by Pillow 12.3 / libjpeg-turbo 3 (4:2:0, 4:2:2, 4:4:4, grey, progressive,
/// restart markers, q5 overshoot, a 3-px-wide image whose chroma is too narrow for fancy upsampling); expected values
/// are SHA-256 hashes of Pillow's RGBA decode. When the bench folder is present, the real WIC decodes are compared too.
/// </summary>
public class CliJpegTests
{
    static readonly Dictionary<string, (string Sha256, string Base64)> Fixtures = new()
    {
        ["s420_q75"] = ("8204b714c73ebf2943648ae1d8b01b0c06bd7d27d02422697cadbd452fdfd5bf", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5
            PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAAR
            CAAdACUDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEG
            E1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG
            h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEB
            AQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYk
            NOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0
            tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDHs5NC8eWT2Wsw2Gja9DBHHZ6nEvkW8yxJ
            gRyqPlU4HDADGFAxtCtgX/hPUdDv2sNStGtplUMQxBDA9NpGQR7jPIPfpSj1bR0GPtWB3xG3PsOK7fw1450rVI49A11vtumLF5cMvlHzrIZH
            zIxGSPu5HPAGAQNp7sTjpUvdpPf7l5Ly/rbftyKEq75quiXS9pP0T3t/w2py0Vgq4XIHr2//AFVpRWAAwBgDgnp+A/z+tamt+GrjR7e3vo54
            r3TLrP2e8gztbk/KwPKvgcqfcZODh+gx/akkV8nysYOexzx+n61xxzOq7xnv37L+vv8AvPtZU6dKmq1GV49fy/PoVY9OLLkjHbG7biiusj00
            suSMdgN23FFZfWL/APDf/av+u2x1wzD3VqfOlbvhRWbUpiqkhYCWIHQblHP4kVhV6H8G4ll8X3QboLBz/wCPx1Nb4GfmtPG/UZLE2vy627nU
            eFdc1XS74Q2cT3lpLkXFg+WilVsK2R0B6Dd+eRkH0S08IWNvFNdaOJWheUs0EgzJF6KCM7h1x1I9zmrtvaozZPbOAO2K1LSEJKsiMVZc4x2x
            Xj/WVdJ7f8Nr+K0/pb1+I3jp80Icie+t79r6Lb0MaPTSVyRjtjdtxRXYJaxXWZWDK2cHacAn1oqo0atRc8FdPrp+qOuOY2Vmf//Z
            """),
        ["s422_q50"] = ("347b8c25928f71e63c2d237ef941b4ae1f5818dc92c9dde014c130eade785542", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1x
            eXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAAR
            CAAdACUDASEAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEG
            E1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG
            h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEB
            AQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYk
            NOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0
            tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCnA1jr0JgvUgsr9I1WG6QbI3CjG1x0H1Hs
            OwBoXGk3NjOYLqIxOADz3HsRXdKsqad9un9f10NsJzVZKMQWADA4qykGB04rNYiMk2v6/r7j24xlSaUidbYkdMfjiisNXq3/AOTW/A71U0Mh
            buzHHm/+On/CtzS9dtbpVsNQPn2wTaj7PngHqpxnHTj2H0M158zVtj5fLYJJ8ztPp/XnsNv9Nks4451dZ7WX/VzJ0PsfQ+3+BpdPXzQwP8GK
            5o+69T3ak1Olz9v+GNJbbI6Y/HFFVq9W/wDya34EKpoec1f0cE3LkAnEZJ9uRWk/hZ4uEaVeN+50+kX13az7IFM8LZEkB5RweDkdB25roodI
            gjV5bLeUZsmNh8ye3uK5r307HoY6oqc+WL+LdenUettkdMfjiistXq3/AOTW/AyVTQ8krofBKhtYlB7QMf8Ax5a7Z/Czxq7tTZ3scSk/TP6V
            ahTa4YHBGcY9q87me/8AXT+vkc9CTLYiSXLEEHODg0V0RoRqrnbav5noqo1of//Z
            """),
        ["s444_q90"] = ("86e0a4db0288c9c960bde8696449e37f2c3959098de2e6ecbb8e757e8f1fa673", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8X
            GBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAAR
            CAAdACUDAREAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEG
            E1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG
            h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEB
            AQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYk
            NOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0
            tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDz/wANXfw8/a08P3GgeOrHw98M/ijYadbW
            uh+NbCP+ztK1CG0g2LaX0KnyoTtXAlRVCgRooXykhk/Va9erlqlRy1ynR2klo1ytzl7NtOcYQu7qXN7NtTk1Wdqfk5fmSqWcmk/636X/AD9N
            /LfFv7Pnij4VeJZPDvinRpND1GKJJmWVlZZEYZUxOhZWXIIypIBDA/MCF4cx4ry/CU3h6NT3UteVOLlyv3YQv8EE3eLeyvU9+u7Uv27hbKsz
            z29TDQ9xOzlJ2ina+vVvukna6vaLu32PhOKEJGGRQcAkHb9APQfy68np5NLi/A4nlwdBulF2TbtC9muWEWnJU4Jtvmb91KVV89WUXD9Zjw9m
            uBh7WXLO3SLbfrZpX9Fq7pJWudfYeEljTATbGvDMBjOP4R6Afp7sePQxubUcNRlTpy5acdJSV4uTi9KdNauMYtro3BtNqWJnGNP0suzBtpt6
            /wBav+vw36Sy8FtLFuZPLA+UKJfKC47e59u3uc18jKrWxT9pUfs0vdUVWVBRS+zfXmkr+9FNqm3ZylUlUk/0jC5mlCyd/lf+v19LHg9p8QvA
            9suz+1wqgclbWYFsdAPk4H/6+T0+hxWcUqdN4fCv3Uldx91ys/dhC/wRT1TesV+8fPWa9n/lnk+VY6WJpQxq9nCUkpS0lypvWTSd5WWtlq7W
            Vlv9JfA/9qvwd48tbX4b/ESUeJ/BsVkbWxvxZkah4eTcuJreYx7ygPlho8t8qIFBVTG/5LL2sFbGx9x6X0uvS3Ty7fcf23h45TiKntOCK/Ji
            aaUnT/eKFWMUo8s1OyvrpK93KTbd3zxq/FL4I6n8N9L0nxBa6haeJvBmtbjpniHTQ3kyYZsRyqwBhm2qSYznBDqCxR9vm1sPKklNO8Xs1/Wj
            P07JuIaGbVauDqQdLEUrc9OVrrbWLWkoXeklvo2kpRvY+Etodegu45suLIptYtyA27CgY/2TzzjOAM4r0MLi1CPtcVUfLTStrqt9IKzabvZT
            15OZqnH2lRX+V4nhDAYqnVpK3tb3VuqtdvXrdXWl7Xb3PXLLwU0sWWTywPlCiXyguO3ufbt7nNbSq1sS/aVH7NL3VFVlQUUvs315pK/vRTap
            t2cpVHUk8cLmaULJ3+V/6/X0sfkZX0R/I56f+z7BLN4u1F443dYtOd5GVSQi+bEuT6DLAZ9SPWvHzRN0Fbv/AJn7B4W16VLPZwqTSc6clFNp
            OT5oysu75YylZa2TeyZ9ofAL4q+MfAfiIWOh2k3iXQrvemp+Fp8yWV5DJsjkLoQVRiNi+aRxwG3KSrfMxx31GDqzfuLddHfTbu9ErK7dlrex
            +6cX5fk1bBvFZlVjQnH4KuinGUbySi9JS6vkWr1cbSs19aeHv2cvD2kWeo6v4HW8msLi8aSXS7pA13ZAgBY1YZMiD5sElmUE8s24jSND2sZY
            2nzckG/dt78WkrpcvNeerTmnKVJc0YRdVtn814rjHGZ3UpSxkleMVG60T7yavZOXWySdlskjVs/BTSRZZPLA+UKJfKAx29z7dvc5rwZVa2Kf
            tKj9ml7qiqyoKKX2b680o396KbVNuzlKpKpJ/S4XM1GFk7/K/wDX6+lj8F6/WD8aPrL/AIJqWMd/8d9bWT7qeG7iT34uLbp6H3rwc5gpYdTf
            2OaXn7sJt8r+zJpcvNZuKbaXNZr4DjWrKlgKbj1qJfhI/UTSNBt5ZAW4CbwqqMbQnp6E569ue5zX5jCvLFVnUqaKn7ZRUdFFUFry78spXajL
            V005Nc1SUqj+KwOLnFJLrb8f6+fpodt4d0xbW9iuYZDDJCX2GMY2hByB9ckc9ieuSa5cNjKs8T9Zvb2XtlFKyUY0FZqKd0nPmaTafJFy0lOc
            qj/SsDippJd7fj/Xz9NDvLfQrLX/ADLuVJIJS5RxA+1WYdWx2z6f1JJ+nwOCo8UxqYytOpSlCUqbVObim4Npy0V1zPXlbai7te9KUpfdUcbV
            oQUY2111P//Z
            """),
        ["gray_q60"] = ("e9616d7d77953b8ec87573de77fe556b4874da43699fe577aead989c81cfd256", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5a
            YVpQYEpRUk//wAALCAAdACUBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQR
            BRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4
            eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEB
            AAA/AKFu2m+JLdrfUY7bT9TjiVYLxB5cUgRcbHHReO4HoB0CnMutFu9OuTbXkBhkAB5xgjtgjgj6fzpUtgMDiraWwA6cVYS1JHTH44rDS9sF
            GPO/JG/wrotG8RWV4i6Xqh+0WYTbG+z95bDPVTjOOnHoB2GC3UtJlsYorlJEuLOfPlTx5wfYjs3HT6+hp2lr5yuG52YwfrWqloSORj8cV5TW
            loQJvJCASBESfbkV2Gh6je2dx5dujXED5Ets3KOpwDkdB25/pxXVQaHbRJJNpwcxs+TGwy6eg9x/n1qVLQkdMfjivDq6n4eoH16YHtbMf/Hl
            r0uKFSfpn9Ku28YVwynBXOMdsVdWFJsuQVOcHaetf//Z
            """),
        ["prog420_q70"] = ("631b77b9b7cd42f71bb574d7dc904834d601cc4fc9db5e833dd4e2623ff20c2f", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7Pj4+JS5E
            SUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wgAR
            CAAhACkDASIAAhEBAxEB/8QAGwAAAwEAAwEAAAAAAAAAAAAAAAMEBQECBgf/xAAZAQACAwEAAAAAAAAAAAAAAAAEBQACAwb/2gAMAwEAAhAD
            EAAAAY4WahN8l1Sh3YyzvNPFPRdTn9qtukObMy/jOnyzSA1f7p4LwW1Box//xAAgEAACAgICAgMAAAAAAAAAAAABAgADBBIQEwUxMjM0/9oA
            CAEBAAEFAl6c0NQ9TBIFgSaQJK7lZLqGqKfIJNOKftpvNQGMpYJNOMJQ+WMNJUnVNAZ18eO/cIIvuf/EAB4RAAICAgIDAAAAAAAAAAAAAAEC
            AAMEQRIhMTIz/9oACAEDAQE/AcMNb41BWAvJDEu6iuEYOdSzPqPy3Et6lnoZVBP/xAAgEQABAwIHAAAAAAAAAAAAAAAAAQISMUEDBBEUMrHw
            /9oACAECAQE/AX4sDcuuTFoSHPVLai0PdGT4qf/EACUQAAIABQMDBQAAAAAAAAAAAAECAAMQEjERIUEEUXEgIlKBsf/aAAgBAQAGPwK2bbJm
            hdFcbK3mLHUqfRiiyeo3QYYZWPknDjBriggoQHRsqYWZJa+Xz3XzTFEBxHMe0aRqNj2rL+/yBAr/AP/EACQQAAEDAgYCAwAAAAAAAAAAAAEA
            EUEQMSFRYXGx8JHRweHx/9oACAEBAAE/IbQGAYQWCNx8AJuswRfbRM4LBUhXVlAEFI9eo09JxODGb9iPADYqQrq1OfwtGyXO2RROVwHD34Uh
            XVqADcn4KfOTtdYAiEQJTFjM9E/I+KdbVWrW4p//2gAMAwEAAgADAAAAEHatEtLXYQ//xAAfEQABBAICAwAAAAAAAAAAAAABABExUSGREIGh
            sfH/2gAIAQMBAT8QKkwk0/VlgCsKJAgT1lOpc8qrdHbq0vHPrhAL/8QAIhEAAQIEBwEAAAAAAAAAAAAAAQARITFh8EFRcYGhscHx/9oACAEC
            AQE/EG8MYaUFL1BCwAeIuuPRviegIZ/l5I5Ago3vWCnLLZOQV//EACMQAQABAwMEAwEAAAAAAAAAAAERACExQVHBEGHw8XGBodH/2gAIAQEA
            AT8QVmyKEQYSAQgIQi4d7HFAUlYZDctSQgBvNCWY3Nfjz9rSIDTavP2qexbVxT6GjAbWG82xIWMYFRvA26epIWMk6OS9APXI+9K0yA02rz9u
            nnd1Q2DvMhdGgZ+G8EQu6OgwsA0VXDfsumQGDavP26ZZhkxMNxUE+DKhD2sUTsQIWws74L81b0OHB3/ivT+n6KHDzXDzXgtun//Z
            """),
        ["prog444_q95"] = ("cdf58e1e3175a67fe88946ba4cf9b221de3737bfbcdeab6f99988d02b8beb8f4", """
            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
            aIl6KGigQf5WyRd/z8xP/9k=
            """),
        ["s420_q5"] = ("f43f88e260d4ade5d3dfe3bc273e637aadf0d4627b0bcf758c6e930b1122517e", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAKBueIx4ZKCMgoy0qqC+8P//8Nzc8P//////////////////////////////////////////////
            ////////////2wBDAaq0tPDS8P//////////////////////////////////////////////////////////////////////////////wAAR
            CAAdACUDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEG
            E1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG
            h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEB
            AQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYk
            NOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0
            tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwBBhxg4Ddj249aaVIOCKMj1pyuD8rcj+VO4
            0NxS4pSuOeoPehaVygxRTsUUDuQU5etNp8X3j9KDO9hykg8cj0qQKO1LilFSPmuNxRT+tFMLn//Z
            """),
        ["narrow420"] = ("93900e38c011fd42e5b1f38044e1abdca955c20cf3932e8f6d0c6985058eea0b", """
            /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8t
            MC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAAR
            CAARAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEG
            E1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWG
            h4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEB
            AQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYk
            NOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0
            tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDv9b8Aah43vY/EsLWdtHqdtb3Ahe4cmPMK
            ZXITBwR17+3Siut+A2rf218HvCt15HkeXZi02792fIJh3ZwPveXux2zjnGaK0Vaola/4L/I+ipcT4+jBUqckoxVl6LYy/wBmL/kh3hr/ALef
            /SmWiiisz50//9k=
            """),
        ["rst420"] = ("7d136dfea0c615a61d16cfb17d1e2e66e091fbcce0d2bd5a61e4cb5f4caa1658", """
            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
            """),
        ["progrst420"] = ("7d136dfea0c615a61d16cfb17d1e2e66e091fbcce0d2bd5a61e4cb5f4caa1658", """
            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
            """),
    };

    public static IEnumerable<object[]> FixtureNames() => Fixtures.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void DecodesLikeLibjpegTurbo(string name)
    {
        var (sha, b64) = Fixtures[name];
        var bytes = Convert.FromBase64String(string.Concat(b64.Where(c => !char.IsWhiteSpace(c))));
        var img = JpegDecoder.TryDecode(bytes);
        Assert.NotNull(img);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(img!.Pixels)).ToLowerInvariant());
    }

    [Fact]
    public void LoadUsesTheLibjpegDecoderForJpegs()
    {
        var (sha, b64) = Fixtures["s420_q75"];
        string path = Path.Combine(Path.GetTempPath(), $"vecto-jpeg-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(string.Concat(b64.Where(c => !char.IsWhiteSpace(c)))));
        try
        {
            var img = ImageIo.Load(path);
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(img.Pixels)).ToLowerInvariant());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnsupportedJpegsFallBack()
    {
        Assert.Null(JpegDecoder.TryDecode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));       // no frame
        Assert.Null(JpegDecoder.TryDecode(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0 })); // not a JPEG
    }

    /// <summary>The bench's JPEGs against their WIC decodes (skipped when the local bench data is absent).</summary>
    [Fact]
    public void MatchesWicDecodesOfBenchJpegs()
    {
        string dir = FindBench();
        if (dir == null) return;
        var report = new List<string>();
        foreach (var jpg in Directory.GetFiles(Path.Combine(dir, "jpg"), "*.jpg").OrderBy(f => f))
        {
            var png = Path.Combine(dir, "wic", Path.GetFileNameWithoutExtension(jpg) + ".png");
            var a = ImageIo.Load(jpg);
            var b = ImageIo.Load(png);
            Assert.Equal((b.Width, b.Height), (a.Width, a.Height));
            int max = 0; long sum = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                int dv = Math.Abs(a.Pixels[i] - b.Pixels[i]);
                max = Math.Max(max, dv); sum += dv;
            }
            if (max > 0) report.Add($"{Path.GetFileName(jpg)} max {max} mean {(double)sum / a.Pixels.Length:F4}");
        }
        Assert.True(report.Count == 0, string.Join("\n", report));
    }

    static string FindBench()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            foreach (var root in new[] { d.FullName, Path.Combine(d.FullName, "repo") })
            {
                var p = Path.Combine(root, "_bench", "jpegdec");
                if (Directory.Exists(Path.Combine(p, "jpg")) && Directory.Exists(Path.Combine(p, "wic"))) return p;
            }
        }
        return null!;
    }
}

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ParametersForm } from "./ParametersForm";

vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (key: string) => key }) }));

describe("Yandex Music parameters", () => {
  afterEach(cleanup);

  it("renders only playlist and masked token parameters and preserves the stored token", () => {
    const onParamChange = vi.fn();
    render(<ParametersForm
      moduleMeta={{ id: "Octockup.Server.Modules.YandexMusicSource", name: "Yandex Music", pathSeparator: "/", requiredParameters: ["playlistUrl", "token"] }}
      params={{ playlistUrl: "https://music.yandex.ru/users/account/playlists/3", token: "" }}
      tag="Music"
      hideTag
      storedSecrets={["token"]}
      privateKeyPassphrase=""
      onParamChange={onParamChange}
      onPrivateKeyPassphraseChange={vi.fn()}
      onTagChange={vi.fn()}
    />);
    const token = screen.getByLabelText("wizard.token");
    expect(token).toHaveAttribute("type", "password");
    expect(token).not.toBeRequired();
    expect(screen.getByLabelText("wizard.playlistUrl", { exact: false })).toBeRequired();
    fireEvent.change(token, { target: { value: "replacement-token" } });
    expect(onParamChange).toHaveBeenCalledWith("token", "replacement-token");
    fireEvent.click(screen.getByRole("button", { name: "moduleEdit.showSecret" }));
    expect(token).toHaveAttribute("type", "text");
  });
});

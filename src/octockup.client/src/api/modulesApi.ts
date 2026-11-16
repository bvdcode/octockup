import { useMemo } from "react";
import type { AxiosInstance } from "axios";
import { useAxios } from "@bvdcode/react-kit";
import { ModuleDestination } from "../types/api";
import type {
  Module,
  ModuleParameters,
  ModuleProviderInfo,
  CreateModuleRequest,
} from "../types/api";

class ModulesApiClient {
  private axiosFactory: () => AxiosInstance;

  constructor(axiosFactory: () => AxiosInstance) {
    this.axiosFactory = axiosFactory;
  }

  private axios(): AxiosInstance {
    return this.axiosFactory();
  }

  async list(): Promise<Module[]> {
    const { data } = await this.axios().get<Module[]>("/api/v1/modules");
    return data;
  }

  async listProviders(): Promise<ModuleProviderInfo[]> {
    const { data } = await this.axios().get<ModuleProviderInfo[]>(
      "/api/v1/modules/providers",
    );
    return data;
  }

  async listProvidersByType(
    type: "source" | "storage",
  ): Promise<ModuleProviderInfo[]> {
    const { data } = await this.axios().get<ModuleProviderInfo[]>(
      `/api/v1/modules/providers/${encodeURIComponent(type)}`,
    );
    return data;
  }

  async create(
    providerId: string,
    destination: ModuleDestination,
    tag: string,
    backupModuleId: string,
    parameters: Record<string, string>,
  ): Promise<void> {
    const body: CreateModuleRequest = {
      destination,
      tag,
      backupModuleId,
      parameters,
    };
    await this.axios().post(
      `/api/v1/modules/providers/${encodeURIComponent(providerId)}`,
      body,
    );
  }

  async test(
    providerId: string,
    parameters: Record<string, string>,
    destination: ModuleDestination,
  ): Promise<void> {
    await this.axios().post(
      `/api/v1/modules/providers/${encodeURIComponent(providerId)}/test`,
      { parameters, destination },
    );
  }

  async getDirectories(
    providerId: string,
    parameters: Record<string, string>,
  ): Promise<string[]> {
    const { data } = await this.axios().post<string[]>(
      `/api/v1/modules/providers/${encodeURIComponent(providerId)}/directories`,
      { parameters },
    );
    return data;
  }

  async delete(moduleId: string): Promise<void> {
    await this.axios().delete(
      `/api/v1/modules/${encodeURIComponent(moduleId)}`,
    );
  }

  async getParameters(moduleId: string): Promise<ModuleParameters> {
    const { data } = await this.axios().get<ModuleParameters>(
      `/api/v1/modules/${encodeURIComponent(moduleId)}/parameters`,
    );
    return data;
  }

  async updateParameters(moduleId: string, parameters: Record<string, string>): Promise<void> {
    await this.axios().patch(
      `/api/v1/modules/${encodeURIComponent(moduleId)}/parameters`, parameters,
    );
  }

  async testParameters(moduleId: string, parameters: Record<string, string>): Promise<void> {
    await this.axios().post(
      `/api/v1/modules/${encodeURIComponent(moduleId)}/parameters/test`, parameters,
    );
  }

  async rename(moduleId: string, newTag: string): Promise<void> {
    await this.axios().patch(
      `/api/v1/modules/${encodeURIComponent(moduleId)}/rename`,
      { newTag },
    );
  }
}

export function useModulesApi(): ModulesApiClient {
  const axios = useAxios();
  return useMemo(() => new ModulesApiClient(() => axios), [axios]);
}

export type { ModulesApiClient };

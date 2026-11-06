/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query: {
      unitPath: string;
      rootId?: string | undefined;
    };

    status: 200;
    /** OK */
    resBody: Types.Graph;
  };
}>;
